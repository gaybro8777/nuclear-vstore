﻿using System;
using System.Collections.Generic;

using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Amazon.S3;
using Amazon.S3.Model;

using ImageSharp;
using ImageSharp.Formats;

using NuClear.VStore.Descriptors;
using NuClear.VStore.Descriptors.Sessions;
using NuClear.VStore.Descriptors.Templates;
using NuClear.VStore.S3;
using NuClear.VStore.Templates;

namespace NuClear.VStore.Sessions
{
    public sealed class SessionManagementService
    {
        private const string SessionToken = "session";

        private static readonly Dictionary<FileFormat, IImageDecoder> ImageDecodersMap =
            new Dictionary<FileFormat, IImageDecoder>
                {
                        { FileFormat.Bmp, new BmpDecoder() },
                        { FileFormat.Gif, new GifDecoder() },
                        { FileFormat.Jpeg, new JpegDecoder() },
                        { FileFormat.Jpg, new JpegDecoder() },
                        { FileFormat.Png, new PngDecoder() }
                };

        private readonly Uri _endpointUri;
        private readonly Uri _fileStorageEndpointUri;
        private readonly string _filesBucketName;
        private readonly IAmazonS3 _amazonS3;
        private readonly TemplateStorageReader _templateStorageReader;

        public SessionManagementService(
            Uri endpointUri,
            Uri fileStorageEndpointUri,
            string filesBucketName,
            IAmazonS3 amazonS3,
            TemplateStorageReader templateStorageReader)
        {
            _endpointUri = endpointUri;
            _fileStorageEndpointUri = fileStorageEndpointUri;
            _filesBucketName = filesBucketName;
            _amazonS3 = amazonS3;
            _templateStorageReader = templateStorageReader;
        }

        public async Task<SessionDescriptor> Setup(long templateId)
        {
            var templateDescriptor = await _templateStorageReader.GetTemplateDescriptor(templateId, null);
            var sessionDescriptor = new SessionDescriptor(_endpointUri, templateDescriptor);

            if (sessionDescriptor.UploadUris.Count == 0)
            {
                throw new SessionCannotBeCreatedException(
                          $"There is no binary content can be uploaded for template '{templateDescriptor.Id}' " +
                          $"with version '{templateDescriptor.VersionId}'");
            }

            var request = new PutObjectRequest
                              {
                                  BucketName = _filesBucketName,
                                  Key = BuildKey(sessionDescriptor.Id, SessionToken),
                                  CannedACL = S3CannedACL.PublicRead
                              };
            var metadataWrapper = MetadataCollectionWrapper.For(request.Metadata);
            metadataWrapper.Write(MetadataElement.TemplateId, templateDescriptor.Id);
            metadataWrapper.Write(MetadataElement.TemplateVersionId, templateDescriptor.VersionId);
            metadataWrapper.Write(MetadataElement.ExpiresAt, sessionDescriptor.ExpiresAt);

            await _amazonS3.PutObjectAsync(request);

            return sessionDescriptor;
        }

        public async Task<MultipartUploadSession> InitiateMultipartUpload(
            Guid sessionId,
            string fileName,
            string contentType,
            long templateId,
            string templateVersionId,
            int templateCode)
        {
            if (!await IsSessionExists(sessionId))
            {
                throw new InvalidOperationException($"Session '{sessionId}' does not exist");
            }

            var metadataResponse = await _amazonS3.GetObjectMetadataAsync(_filesBucketName, BuildKey(sessionId, SessionToken));
            var metadataWrapper = MetadataCollectionWrapper.For(metadataResponse.Metadata);

            var expiresAt = metadataWrapper.Read<DateTime>(MetadataElement.ExpiresAt);
            if (SessionDescriptor.IsSessionExpired(expiresAt))
            {
                throw new SessionExpiredException(sessionId);
            }

            var sessionTemplateId = metadataWrapper.Read<long>(MetadataElement.TemplateId);
            if (sessionTemplateId != templateId)
            {
                throw new InvalidTemplateException($"Template '{sessionTemplateId}' is expected for session '{sessionId}'");
            }

            var sessionTemplateVersionId = metadataWrapper.Read<string>(MetadataElement.TemplateVersionId);
            if (!sessionTemplateVersionId.Equals(templateVersionId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidTemplateException($"Template version Id '{sessionTemplateVersionId}' is expected for session '{sessionId}'");
            }

            var key = BuildKey(sessionId, fileName);
            var request = new InitiateMultipartUploadRequest
                              {
                                  BucketName = _filesBucketName,
                                  Key = key,
                                  ContentType = contentType
                              };
            metadataWrapper = MetadataCollectionWrapper.For(request.Metadata);
            metadataWrapper.Write(MetadataElement.Filename, fileName);

            var uploadResponse = await _amazonS3.InitiateMultipartUploadAsync(request);

            return new MultipartUploadSession(sessionId, fileName, uploadResponse.UploadId);
        }

        public async Task UploadFilePart(MultipartUploadSession uploadSession, Stream inputStream, long templateId, string templateVersionId, int templateCode)
        {
            using (var memory = new MemoryStream())
            {
                await inputStream.CopyToAsync(memory);
                memory.Position = 0;

                if (uploadSession.NextPartNumber == 1)
                {
                    var elementDescriptor = await GetElementDescriptor(templateId, templateVersionId, templateCode);
                    EnsureFileHeaderIsValid(elementDescriptor, memory);
                }

                var key = BuildKey(uploadSession.SessionId, uploadSession.FileName);
                var response = await _amazonS3.UploadPartAsync(
                                   new UploadPartRequest
                                       {
                                           BucketName = _filesBucketName,
                                           Key = key,
                                           UploadId = uploadSession.UploadId,
                                           InputStream = memory,
                                           PartNumber = uploadSession.NextPartNumber
                                       });
                uploadSession.AddPart(response.ETag);
            }
        }

        public async Task AbortMultipartUpload(MultipartUploadSession uploadSession)
        {
            if (!uploadSession.IsCompleted)
            {
                var key = BuildKey(uploadSession.SessionId, uploadSession.FileName);
                await _amazonS3.AbortMultipartUploadAsync(_filesBucketName, key, uploadSession.UploadId);
            }
        }

        public async Task<UploadedFileInfo> CompleteMultipartUpload(MultipartUploadSession uploadSession, long templateId, string templateVersionId, int templateCode)
        {
            var uploadKey = BuildKey(uploadSession.SessionId, uploadSession.FileName);
            var partETags = uploadSession.Parts.Select(x => new PartETag(x.PartNumber, x.Etag)).ToList();
            var uploadResponse = await _amazonS3.CompleteMultipartUploadAsync(
                                     new CompleteMultipartUploadRequest
                                         {
                                             BucketName = _filesBucketName,
                                             Key = uploadKey,
                                             UploadId = uploadSession.UploadId,
                                             PartETags = partETags
                                         });
            uploadSession.Complete();

            var elementDescriptor = await GetElementDescriptor(templateId, templateVersionId, templateCode);
            try
            {
                var getResponse = await _amazonS3.GetObjectAsync(_filesBucketName, uploadKey);
                using (getResponse.ResponseStream)
                {
                    EnsureUploadedFileIsValid(elementDescriptor.Type, elementDescriptor.Constraints, getResponse.ResponseStream, getResponse.ContentLength);
                }

                var fileKey = string.Concat(BuildKey(uploadSession.SessionId, uploadResponse.ETag), Path.GetExtension(uploadSession.FileName));
                var copyRequest = new CopyObjectRequest
                                      {
                                          SourceBucket = _filesBucketName,
                                          SourceKey = uploadKey,
                                          DestinationBucket = _filesBucketName,
                                          DestinationKey = fileKey,
                                          CannedACL = S3CannedACL.PublicRead
                                      };
                await _amazonS3.CopyObjectAsync(copyRequest);

                return new UploadedFileInfo(uploadResponse.ETag, new Uri(_fileStorageEndpointUri, fileKey));
            }
            finally
            {
                await _amazonS3.DeleteObjectAsync(_filesBucketName, uploadKey);
            }
        }

        private static string BuildKey(Guid sessionId, string fileName) => $"{sessionId}/{fileName}";

        private static void EnsureFileHeaderIsValid(IElementDescriptor elementDescriptor, Stream inputStream)
        {
            if (elementDescriptor.Type == ElementDescriptorType.Image)
            {
                var maxHeaderSize = ImageDecodersMap.Values.Max(x => x.HeaderSize);
                var header = new byte[maxHeaderSize];

                var position = inputStream.Position;
                inputStream.Read(header, 0, header.Length);
                inputStream.Position = position;

                if (!ImageDecodersMap.Values.Any(x => x.IsSupportedFileFormat(header.Take(x.HeaderSize).ToArray())))
                {
                    throw new ImageIncorrectException("Input stream cannot be recognized as image.");
                }
            }
        }

        private static void EnsureUploadedFileIsValid(ElementDescriptorType elementDescriptorType, IConstraintSet elementDescriptorConstraints, Stream inputStream, long inputStreamLength)
        {
            switch (elementDescriptorType)
            {
                case ElementDescriptorType.Image:
                    ValidateImage((ImageElementConstraints)elementDescriptorConstraints, inputStream, inputStreamLength);
                    break;
                case ElementDescriptorType.Article:
                    ValidateArticle((ArticleElementConstraints)elementDescriptorConstraints, inputStream, inputStreamLength);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(elementDescriptorType));
            }
        }

        private static void ValidateImage(ImageElementConstraints constraints, Stream inputStream, long inputStreamLength)
        {
            if (inputStreamLength > constraints.MaxSize)
            {
                throw new FilesizeMismatchException("Image exceeds the size limit.");
            }

            Image image;
            try
            {
                image = new Image(inputStream);
            }
            catch (Exception ex)
            {
                throw new ImageIncorrectException("Image cannot be loaded from the stream.", ex);
            }

            var supportedDecoders = constraints.SupportedFileFormats
                                                .Aggregate(
                                                    new List<IImageDecoder>(),
                                                    (result, next) =>
                                                        {
                                                            IImageDecoder imageDecoder;
                                                            if (ImageDecodersMap.TryGetValue(next, out imageDecoder))
                                                            {
                                                                result.Add(imageDecoder);
                                                            }

                                                            return result;
                                                        });

            if (!supportedDecoders.Exists(x => x.GetType() == image.CurrentImageFormat.Decoder.GetType()))
            {
                throw new ImageIncorrectException("Image has an incorrect format");
            }

            if (image.Width > constraints.ImageSize.Width || image.Height > constraints.ImageSize.Height)
            {
                throw new ImageIncorrectException("Image has an incorrect size");
            }
        }

        private static void ValidateArticle(ArticleElementConstraints constraints, Stream inputStream, long inputStreamLength)
        {
            if (inputStreamLength > constraints.MaxSize)
            {
                throw new FilesizeMismatchException("Article exceeds the size limit.");
            }
        }

        private async Task<bool> IsSessionExists(Guid sessionId)
        {
            var response = await _amazonS3.ListObjectsAsync(
                               new ListObjectsRequest
                                   {
                                       BucketName = _filesBucketName,
                                       Prefix = sessionId.ToString()
                                   });
            return response.S3Objects.Count > 0;
        }

        private async Task<IElementDescriptor> GetElementDescriptor(long templateId, string templateVersionId, int templateCode)
        {
            var templateDescriptor = await _templateStorageReader.GetTemplateDescriptor(templateId, templateVersionId);
            return templateDescriptor.Elements.Single(x => x.TemplateCode == templateCode);
        }
    }
}