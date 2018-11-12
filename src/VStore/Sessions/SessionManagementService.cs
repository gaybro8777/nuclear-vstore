using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.Caching.Memory;

using Newtonsoft.Json;

using NuClear.VStore.DataContract;
using NuClear.VStore.Descriptors;
using NuClear.VStore.Descriptors.Sessions;
using NuClear.VStore.Descriptors.Templates;
using NuClear.VStore.Events;
using NuClear.VStore.Http;
using NuClear.VStore.Json;
using NuClear.VStore.Kafka;
using NuClear.VStore.Options;
using NuClear.VStore.Prometheus;
using NuClear.VStore.S3;
using NuClear.VStore.Sessions.ContentValidation;
using NuClear.VStore.Sessions.ContentValidation.Errors;
using NuClear.VStore.Sessions.Fetch;
using NuClear.VStore.Sessions.Upload;
using NuClear.VStore.Templates;

using Polly.Timeout;

using Prometheus.Client;

namespace NuClear.VStore.Sessions
{
    public sealed class SessionManagementService
    {
        private readonly TimeSpan _sessionExpiration;
        private readonly string _filesBucketName;
        private readonly string _sessionsTopicName;
        private readonly ICephS3Client _cephS3Client;
        private readonly IFetchClient _fetchClient;
        private readonly SessionStorageReader _sessionStorageReader;
        private readonly ITemplatesStorageReader _templatesStorageReader;
        private readonly IEventSender _eventSender;
        private readonly IMemoryCache _memoryCache;
        private readonly Counter _uploadedBinariesMetric;
        private readonly Counter _createdSessionsMetric;

        public SessionManagementService(
            CephOptions cephOptions,
            SessionOptions sessionOptions,
            KafkaOptions kafkaOptions,
            ICephS3Client cephS3Client,
            IFetchClient fetchClient,
            SessionStorageReader sessionStorageReader,
            ITemplatesStorageReader templatesStorageReader,
            IEventSender eventSender,
            MetricsProvider metricsProvider,
            IMemoryCache memoryCache)
        {
            _sessionExpiration = sessionOptions.SessionExpiration;
            _filesBucketName = cephOptions.FilesBucketName;
            _sessionsTopicName = kafkaOptions.SessionEventsTopic;
            _cephS3Client = cephS3Client;
            _fetchClient = fetchClient;
            _sessionStorageReader = sessionStorageReader;
            _templatesStorageReader = templatesStorageReader;
            _eventSender = eventSender;
            _memoryCache = memoryCache;
            _uploadedBinariesMetric = metricsProvider.GetUploadedBinariesMetric();
            _createdSessionsMetric = metricsProvider.GetCreatedSessionsMetric();
        }

        public async Task<SessionContext> GetSessionContext(Guid sessionId)
        {
            var (sessionDescriptor, authorInfo, expiresAt) = await _sessionStorageReader.GetSessionDescriptor(sessionId);
            var templateDescriptor = await _templatesStorageReader.GetTemplateDescriptor(sessionDescriptor.TemplateId, sessionDescriptor.TemplateVersionId);

            return new SessionContext(
                templateDescriptor.Id,
                templateDescriptor,
                sessionDescriptor.Language,
                authorInfo,
                expiresAt);
        }

        public async Task Setup(Guid sessionId, long templateId, string templateVersionId, Language language, AuthorInfo authorInfo)
        {
            if (language == Language.Unspecified)
            {
                throw new SessionCannotBeCreatedException("Language must be explicitly specified.");
            }

            var templateDescriptor = await _templatesStorageReader.GetTemplateDescriptor(templateId, templateVersionId);
            var sessionDescriptor = new SessionDescriptor
                                        {
                                            TemplateId = templateDescriptor.Id,
                                            TemplateVersionId = templateDescriptor.VersionId,
                                            Language = language,
                                            BinaryElementTemplateCodes = templateDescriptor.GetBinaryElementTemplateCodes()
                                        };
            var request = new PutObjectRequest
                              {
                                  BucketName = _filesBucketName,
                                  Key = sessionId.AsS3ObjectKey(Tokens.SessionPostfix),
                                  CannedACL = S3CannedACL.PublicRead,
                                  ContentType = ContentType.Json,
                                  ContentBody = JsonConvert.SerializeObject(sessionDescriptor, SerializerSettings.Default)
                              };

            var expiresAt = SessionDescriptor.CurrentTime().Add(_sessionExpiration);
            var metadataWrapper = MetadataCollectionWrapper.For(request.Metadata);
            metadataWrapper.Write(MetadataElement.ExpiresAt, expiresAt);
            metadataWrapper.Write(MetadataElement.Author, authorInfo.Author);
            metadataWrapper.Write(MetadataElement.AuthorLogin, authorInfo.AuthorLogin);
            metadataWrapper.Write(MetadataElement.AuthorName, authorInfo.AuthorName);

            await _eventSender.SendAsync(_sessionsTopicName, new SessionCreatingEvent(sessionId, expiresAt));

            await _cephS3Client.PutObjectAsync(request);
            _createdSessionsMetric.Inc();
        }

        /// <summary>
        /// Initiate upload session
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="templateCode">Element's template code</param>
        /// <param name="uploadedFileMetadata">File metadata</param>
        /// <exception cref="MissingFilenameException">Filename is not specified</exception>
        /// <exception cref="InvalidTemplateException">Binary content is not expected for specified template code</exception>
        /// <exception cref="ObjectNotFoundException">Session or template has not been found</exception>
        /// <exception cref="SessionExpiredException">Specified session is expired</exception>
        /// <exception cref="S3Exception">Error making request to S3</exception>
        /// <exception cref="InvalidBinaryException">Binary does not meet template's constraints</exception>
        /// <returns>Initiated upload session instance</returns>
        public async Task<MultipartUploadSession> InitiateMultipartUpload(
            Guid sessionId,
            int templateCode,
            IUploadedFileMetadata uploadedFileMetadata)
        {
            if (string.IsNullOrEmpty(uploadedFileMetadata.FileName))
            {
                throw new MissingFilenameException($"Filename has not been provided for the item '{templateCode}'");
            }

            var (sessionDescriptor, _, expiresAt) = await _sessionStorageReader.GetSessionDescriptor(sessionId);
            if (sessionDescriptor.BinaryElementTemplateCodes.All(x => x != templateCode))
            {
                throw new InvalidTemplateException(
                    $"Binary content is not expected for the item '{templateCode}' within template '{sessionDescriptor.TemplateId}' " +
                    $"with version Id '{sessionDescriptor.TemplateVersionId}'.");
            }

            var elementDescriptor = await GetElementDescriptor(sessionDescriptor.TemplateId, sessionDescriptor.TemplateVersionId, templateCode);
            EnsureFileMetadataIsValid(elementDescriptor, sessionDescriptor.Language, uploadedFileMetadata);

            var fileKey = Guid.NewGuid().ToString();
            var key = sessionId.AsS3ObjectKey(fileKey);
            var request = new InitiateMultipartUploadRequest
                {
                    BucketName = _filesBucketName,
                    Key = key,
                    ContentType = uploadedFileMetadata.ContentType
                };
            var metadataWrapper = MetadataCollectionWrapper.For(request.Metadata);
            metadataWrapper.Write(MetadataElement.Filename, uploadedFileMetadata.FileName);

            var uploadResponse = await _cephS3Client.InitiateMultipartUploadAsync(request);

            return new MultipartUploadSession(
                sessionId,
                sessionDescriptor,
                expiresAt,
                elementDescriptor,
                uploadedFileMetadata,
                fileKey,
                uploadResponse.UploadId);
        }

        public async Task UploadFilePart(MultipartUploadSession uploadSession, Stream inputStream, int templateCode)
        {
            if (SessionDescriptor.IsSessionExpired(uploadSession.SessionExpiresAt))
            {
                throw new SessionExpiredException(uploadSession.SessionId, uploadSession.SessionExpiresAt);
            }

            if (uploadSession.NextPartNumber == 1)
            {
                var sessionDescriptor = uploadSession.SessionDescriptor;
                var elementDescriptor = uploadSession.ElementDescriptor;
                EnsureFileHeaderIsValid(
                    templateCode,
                    uploadSession.ElementDescriptor.Type,
                    elementDescriptor.Constraints.For(sessionDescriptor.Language),
                    inputStream,
                    uploadSession.UploadedFileMetadata);
            }

            var key = uploadSession.SessionId.AsS3ObjectKey(uploadSession.FileKey);
            inputStream.Position = 0;

            var response = await _cephS3Client.UploadPartAsync(
                                new UploadPartRequest
                                    {
                                        BucketName = _filesBucketName,
                                        Key = key,
                                        UploadId = uploadSession.UploadId,
                                        InputStream = inputStream,
                                        PartNumber = uploadSession.NextPartNumber
                                    });
            uploadSession.AddPart(response.ETag);
        }

        public async Task AbortMultipartUpload(MultipartUploadSession uploadSession)
        {
            if (!uploadSession.IsCompleted)
            {
                var key = uploadSession.SessionId.AsS3ObjectKey(uploadSession.FileKey);
                await _cephS3Client.AbortMultipartUploadAsync(_filesBucketName, key, uploadSession.UploadId);
            }
        }

        public async Task<string> CompleteMultipartUpload(MultipartUploadSession uploadSession)
        {
            var uploadKey = uploadSession.SessionId.AsS3ObjectKey(uploadSession.FileKey);
            var partETags = uploadSession.Parts.Select(x => new PartETag(x.PartNumber, x.Etag)).ToList();
            var uploadResponse = await _cephS3Client.CompleteMultipartUploadAsync(
                                     new CompleteMultipartUploadRequest
                                         {
                                             BucketName = _filesBucketName,
                                             Key = uploadKey,
                                             UploadId = uploadSession.UploadId,
                                             PartETags = partETags
                                         });
            uploadSession.Complete();

            if (SessionDescriptor.IsSessionExpired(uploadSession.SessionExpiresAt))
            {
                throw new SessionExpiredException(uploadSession.SessionId, uploadSession.SessionExpiresAt);
            }

            try
            {
                using (var getResponse = await _cephS3Client.GetObjectAsync(_filesBucketName, uploadKey))
                {
                    var memoryStream = new MemoryStream();
                    using (getResponse.ResponseStream)
                    {
                        getResponse.ResponseStream.CopyTo(memoryStream);
                        memoryStream.Position = 0;
                    }

                    using (memoryStream)
                    {
                        var sessionDescriptor = uploadSession.SessionDescriptor;
                        var elementDescriptor = uploadSession.ElementDescriptor;
                        EnsureFileContentIsValid(
                            elementDescriptor.TemplateCode,
                            elementDescriptor.Type,
                            elementDescriptor.Constraints.For(sessionDescriptor.Language),
                            memoryStream,
                            uploadSession.UploadedFileMetadata);
                    }

                    var metadataWrapper = MetadataCollectionWrapper.For(getResponse.Metadata);
                    var fileName = metadataWrapper.Read<string>(MetadataElement.Filename);

                    var fileExtension = Path.GetExtension(fileName).ToLowerInvariant();
                    var fileKey = Path.ChangeExtension(uploadSession.SessionId.AsS3ObjectKey(uploadResponse.ETag), fileExtension);
                    var copyRequest = new CopyObjectRequest
                        {
                            ContentType = uploadSession.UploadedFileMetadata.ContentType,
                            SourceBucket = _filesBucketName,
                            SourceKey = uploadKey,
                            DestinationBucket = _filesBucketName,
                            DestinationKey = fileKey,
                            MetadataDirective = S3MetadataDirective.REPLACE,
                            CannedACL = S3CannedACL.PublicRead
                        };

                    foreach (var metadataKey in getResponse.Metadata.Keys)
                    {
                        copyRequest.Metadata.Add(metadataKey, getResponse.Metadata[metadataKey]);
                    }

                    await _cephS3Client.CopyObjectAsync(copyRequest);
                    _uploadedBinariesMetric.Inc();

                    _memoryCache.Set(fileKey, new BinaryMetadata(fileName, getResponse.ContentLength), uploadSession.SessionExpiresAt);

                    return fileKey;
                }
            }
            finally
            {
                await _cephS3Client.DeleteObjectAsync(_filesBucketName, uploadKey);
            }
        }

        /// <summary>
        /// Fetch file for created session
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="templateCode">Element's template code</param>
        /// <param name="fetchParameters">Fetch parameters</param>
        /// <exception cref="InvalidTemplateException">Specified template code does not refer to binary element</exception>
        /// <exception cref="InvalidFetchUrlException">Fetch URL is invalid</exception>
        /// <exception cref="MissingFilenameException">Filename is not specified</exception>
        /// <exception cref="ObjectNotFoundException">Session or template has not been found</exception>
        /// <exception cref="SessionExpiredException">Specified session is expired</exception>
        /// <exception cref="S3Exception">Error making request to S3</exception>
        /// <exception cref="InvalidBinaryException">Binary does not meet template's constraints</exception>
        /// <exception cref="FetchFailedException">Fetch request failed</exception>
        /// <returns>Fetched file key</returns>
        public async Task<string> FetchFile(Guid sessionId, int templateCode, FetchParameters fetchParameters)
        {
            if (!TryCreateUri(fetchParameters, out var fetchUri))
            {
                throw new InvalidFetchUrlException(
                    $"Fetch URI must be a valid absolute URI with '{Uri.UriSchemeHttp}' or '{Uri.UriSchemeHttps}' scheme");
            }

            await _sessionStorageReader.GetSessionDescriptor(sessionId);    // Firstly ensure that specified session exists
            MultipartUploadSession uploadSession = null;
            try
            {
                var (stream, mediaType) = await _fetchClient.FetchAsync(fetchUri);
                var fileMetadata = new GenericUploadedFileMetadata(FileType.NotSet, fetchParameters.FileName, mediaType, stream.Length);
                uploadSession = await InitiateMultipartUpload(sessionId, templateCode, fileMetadata);
                await UploadFilePart(uploadSession, stream, templateCode);
                var uploadedFileKey = await CompleteMultipartUpload(uploadSession);
                return uploadedFileKey;
            }
            catch (FetchResponseTooLargeException ex)
            {
                throw new InvalidBinaryException(templateCode, new BinaryTooLargeError(ex.ContentLength));
            }
            catch (FetchRequestException ex)
            {
                throw new FetchFailedException($"Fetch request failed with status code {ex.StatusCode} and content: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                throw new FetchFailedException($"Fetch request failed: {ex.Message}");
            }
            catch (TimeoutRejectedException)
            {
                throw new FetchFailedException("Fetch request failed: request timeout exceeded");
            }
            catch (FetchResponseContentTypeInvalidException ex)
            {
                throw new FetchFailedException($"Fetch request failed: {ex.Message}");
            }
            finally
            {
                if (uploadSession != null)
                {
                    await AbortMultipartUpload(uploadSession);
                }
            }
        }

        private static bool TryCreateUri(FetchParameters fetchParameters, out Uri fetchUri)
            => Uri.TryCreate(fetchParameters.Url, UriKind.Absolute, out fetchUri) &&
               fetchUri.IsWellFormedOriginalString() &&
               (fetchUri.Scheme == Uri.UriSchemeHttp || fetchUri.Scheme == Uri.UriSchemeHttps);

        private static void EnsureFileMetadataIsValid(IElementDescriptor elementDescriptor, Language language, IUploadedFileMetadata uploadedFileMetadata)
        {
            var constraints = (IBinaryElementConstraints)elementDescriptor.Constraints.For(language);
            if (constraints.MaxFilenameLength < uploadedFileMetadata.FileName.Length)
            {
                throw new InvalidBinaryException(elementDescriptor.TemplateCode, new FilenameTooLongError(uploadedFileMetadata.FileName.Length));
            }

            if (constraints.MaxSize < uploadedFileMetadata.FileLength)
            {
                throw new InvalidBinaryException(elementDescriptor.TemplateCode, new BinaryTooLargeError(uploadedFileMetadata.FileLength));
            }

            if (constraints is CompositeBitmapImageElementConstraints compositeBitmapImageElementConstraints &&
                uploadedFileMetadata.FileType == FileType.SizeSpecificBitmapImage &&
                compositeBitmapImageElementConstraints.SizeSpecificImageMaxSize < uploadedFileMetadata.FileLength)
            {
                throw new InvalidBinaryException(elementDescriptor.TemplateCode, new SizeSpecificImageTooLargeError(uploadedFileMetadata.FileLength));
            }

            var extension = GetDotLessExtension(uploadedFileMetadata.FileName);
            if (!ValidateFileExtension(extension, constraints))
            {
                throw new InvalidBinaryException(elementDescriptor.TemplateCode, new BinaryInvalidFormatError(extension));
            }
        }

        private static void EnsureFileHeaderIsValid(
            int templateCode,
            ElementDescriptorType elementDescriptorType,
            IElementConstraints elementConstraints,
            Stream inputStream,
            IUploadedFileMetadata uploadedFileMetadata)
        {
            var fileFormat = DetectFileFormat(uploadedFileMetadata.FileName);
            switch (elementDescriptorType)
            {
                case ElementDescriptorType.BitmapImage:
                    BitmapImageValidator.ValidateBitmapImageHeader(
                        templateCode,
                        (BitmapImageElementConstraints)elementConstraints,
                        fileFormat,
                        inputStream);
                    break;
                case ElementDescriptorType.ScalableBitmapImage:
                    BitmapImageValidator.ValidateSizeRangedBitmapImageHeader(
                        templateCode,
                        (ScalableBitmapImageElementConstraints)elementConstraints,
                        fileFormat,
                        inputStream);
                    break;
                case ElementDescriptorType.CompositeBitmapImage:
                    if (uploadedFileMetadata.FileType == FileType.SizeSpecificBitmapImage)
                    {
                        var imageMetadata = (UploadedImageMetadata)uploadedFileMetadata;
                        BitmapImageValidator.ValidateSizeSpecificBitmapImageHeader(
                            templateCode,
                            (CompositeBitmapImageElementConstraints)elementConstraints,
                            fileFormat,
                            inputStream,
                            imageMetadata.Size);
                    }
                    else
                    {
                        BitmapImageValidator.ValidateSizeRangedBitmapImageHeader(
                            templateCode,
                            (CompositeBitmapImageElementConstraints)elementConstraints,
                            fileFormat,
                            inputStream);
                    }

                    break;
                case ElementDescriptorType.VectorImage:
                    VectorImageValidator.ValidateVectorImageHeader(templateCode, fileFormat, inputStream);
                    break;
                case ElementDescriptorType.Article:
                    break;
                case ElementDescriptorType.PlainText:
                case ElementDescriptorType.FormattedText:
                case ElementDescriptorType.FasComment:
                case ElementDescriptorType.Link:
                case ElementDescriptorType.Phone:
                case ElementDescriptorType.VideoLink:
                case ElementDescriptorType.Color:
                    throw new NotSupportedException($"Not binary element descriptor type {elementDescriptorType}");
                default:
                    throw new ArgumentOutOfRangeException(nameof(elementDescriptorType), elementDescriptorType, "Unsupported element descriptor type");
            }
        }

        private static void EnsureFileContentIsValid(
            int templateCode,
            ElementDescriptorType elementDescriptorType,
            IElementConstraints elementConstraints,
            Stream inputStream,
            IUploadedFileMetadata uploadedFileMetadata)
        {
            switch (elementDescriptorType)
            {
                case ElementDescriptorType.BitmapImage:
                    BitmapImageValidator.ValidateBitmapImage(templateCode, (BitmapImageElementConstraints)elementConstraints, inputStream);
                    break;
                case ElementDescriptorType.VectorImage:
                    var fileFormat = DetectFileFormat(uploadedFileMetadata.FileName);
                    VectorImageValidator.ValidateVectorImage(templateCode, fileFormat, (VectorImageElementConstraints)elementConstraints, inputStream);
                    break;
                case ElementDescriptorType.Article:
                    ArticleValidator.ValidateArticle(templateCode, inputStream);
                    break;
                case ElementDescriptorType.CompositeBitmapImage:
                    break;
                case ElementDescriptorType.ScalableBitmapImage:
                    break;
                case ElementDescriptorType.PlainText:
                case ElementDescriptorType.FormattedText:
                case ElementDescriptorType.FasComment:
                case ElementDescriptorType.Link:
                case ElementDescriptorType.Phone:
                case ElementDescriptorType.VideoLink:
                case ElementDescriptorType.Color:
                    throw new NotSupportedException($"Not binary element descriptor type {elementDescriptorType}");
                default:
                    throw new ArgumentOutOfRangeException(nameof(elementDescriptorType), elementDescriptorType, "Unsupported element descriptor type");
            }
        }

        private static FileFormat DetectFileFormat(string fileName)
        {
            var extension = GetDotLessExtension(fileName);
            if (Enum.TryParse(extension, true, out FileFormat format))
            {
                return format;
            }

            throw new ArgumentException($"Filename '{fileName}' does not have appropriate extension", nameof(fileName));
        }

        private static bool ValidateFileExtension(string extension, IBinaryElementConstraints constraints)
        {
            return Enum.TryParse(extension, true, out FileFormat format)
                   && Enum.IsDefined(typeof(FileFormat), format)
                   && format.ToString().Equals(extension, StringComparison.OrdinalIgnoreCase)
                   && constraints.SupportedFileFormats.Any(f => f == format);
        }

        private static string GetDotLessExtension(string path)
        {
            var dottedExtension = Path.GetExtension(path);
            return string.IsNullOrEmpty(dottedExtension)
                       ? dottedExtension
                       : dottedExtension.Trim('.').ToLowerInvariant();
        }

        private async Task<IElementDescriptor> GetElementDescriptor(long templateId, string templateVersionId, int templateCode)
        {
            var templateDescriptor = await _templatesStorageReader.GetTemplateDescriptor(templateId, templateVersionId);
            return templateDescriptor.Elements.Single(x => x.TemplateCode == templateCode);
        }
    }
}