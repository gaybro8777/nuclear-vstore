using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

using Newtonsoft.Json.Linq;

using NuClear.VStore.DataContract;
using NuClear.VStore.Descriptors;
using NuClear.VStore.Descriptors.Objects;
using NuClear.VStore.Descriptors.Sessions;
using NuClear.VStore.Http.Core.Controllers;
using NuClear.VStore.Http.Core.Filters;
using NuClear.VStore.Json;
using NuClear.VStore.Options;
using NuClear.VStore.S3;
using NuClear.VStore.Sessions;
using NuClear.VStore.Sessions.Fetch;
using NuClear.VStore.Sessions.Upload;

namespace NuClear.VStore.Host.Controllers
{
    [ApiVersion("1.1")]
    [ApiVersion("1.0", Deprecated = true)]
    [Route("api/{api-version:apiVersion}/sessions")]
    public sealed class SessionsController : VStoreController
    {
        private readonly CdnOptions _cdnOptions;
        private readonly SessionManagementService _sessionManagementService;
        private readonly ILogger<SessionsController> _logger;

        public SessionsController(CdnOptions cdnOptions, SessionManagementService sessionManagementService, ILogger<SessionsController> logger)
        {
            _cdnOptions = cdnOptions;
            _sessionManagementService = sessionManagementService;
            _logger = logger;
        }

        /// <summary>
        /// Get specific session (old API)
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <returns>Session descriptor</returns>
        [Obsolete, MapToApiVersion("1.0")]
        [HttpGet("{sessionId:guid}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status410Gone)]
        public async Task<IActionResult> GetV10(Guid sessionId)
        {
            try
            {
                var sessionContext = await _sessionManagementService.GetSessionContext(sessionId);

                var templateDescriptor = sessionContext.TemplateDescriptor;
                var uploadUrls = UploadUrl.Generate(
                    templateDescriptor,
                    templateCode => Url.Action(
                        nameof(UploadFile),
                        new { sessionId, templateCode }));

                Response.Headers[HeaderNames.ETag] = $"\"{sessionId}\"";
                Response.Headers[HeaderNames.Expires] = sessionContext.ExpiresAt.ToString("R");

                return Json(
                    new
                        {
                            sessionContext.AuthorInfo.Author,
                            sessionContext.AuthorInfo.AuthorLogin,
                            sessionContext.AuthorInfo.AuthorName,
                            sessionContext.Language,
                            Template = new
                                {
                                    Id = sessionContext.TemplateId,
                                    templateDescriptor.VersionId,
                                    templateDescriptor.LastModified,
                                    templateDescriptor.Author,
                                    templateDescriptor.AuthorLogin,
                                    templateDescriptor.AuthorName,
                                    templateDescriptor.Properties,
                                    templateDescriptor.Elements
                                },
                            uploadUrls
                        });
            }
            catch (ObjectNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (SessionExpiredException ex)
            {
                return Gone(ex.ExpiredAt);
            }
        }

        /// <summary>
        /// Get specific session
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <returns>Session descriptor</returns>
        [MapToApiVersion("1.1")]
        [HttpGet("{sessionId:guid}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status410Gone)]
        public async Task<IActionResult> Get(Guid sessionId)
        {
            try
            {
                var sessionContext = await _sessionManagementService.GetSessionContext(sessionId);

                var templateDescriptor = sessionContext.TemplateDescriptor;
                var uploadUrls = UploadUrl.Generate(
                    templateDescriptor,
                    templateCode => Url.Action(
                        nameof(UploadFile),
                        new { sessionId, templateCode }));

                var fetchUrls = FetchUrl.Generate(
                    templateDescriptor,
                    templateCode => Url.Action(
                        nameof(FetchFile),
                        new { sessionId, templateCode }));

                Response.Headers[HeaderNames.ETag] = $"\"{sessionId}\"";
                Response.Headers[HeaderNames.Expires] = sessionContext.ExpiresAt.ToString("R");

                return Json(
                    new
                        {
                            sessionContext.AuthorInfo.Author,
                            sessionContext.AuthorInfo.AuthorLogin,
                            sessionContext.AuthorInfo.AuthorName,
                            sessionContext.Language,
                            Template = new
                                {
                                    Id = sessionContext.TemplateId,
                                    templateDescriptor.VersionId,
                                    templateDescriptor.LastModified,
                                    templateDescriptor.Author,
                                    templateDescriptor.AuthorLogin,
                                    templateDescriptor.AuthorName,
                                    templateDescriptor.Properties,
                                    templateDescriptor.Elements
                                },
                            uploadUrls,
                            fetchUrls
                        });
            }
            catch (ObjectNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (SessionExpiredException ex)
            {
                return Gone(ex.ExpiredAt);
            }
        }

        /// <summary>
        /// Create session for uploading file(-s) using latest version of template
        /// </summary>
        /// <param name="apiVersion">API version</param>
        /// <param name="author">Author identifier</param>
        /// <param name="authorLogin">Author login</param>
        /// <param name="authorName">Author name</param>
        /// <param name="language">Language of session</param>
        /// <param name="templateId">Template identifier</param>
        /// <returns>HTTP code</returns>
        [HttpPost("{language:lang}/{templateId:long}")]
        [ProducesResponseType(201)]
        [ProducesResponseType(typeof(string), 400)]
        [ProducesResponseType(typeof(string), 404)]
        public async Task<IActionResult> SetupSession(
            ApiVersion apiVersion,
            [FromHeader(Name = Http.HeaderNames.AmsAuthor)] string author,
            [FromHeader(Name = Http.HeaderNames.AmsAuthorLogin)] string authorLogin,
            [FromHeader(Name = Http.HeaderNames.AmsAuthorName)] string authorName,
            Language language,
            long templateId)
        {
            if (string.IsNullOrEmpty(author) || string.IsNullOrEmpty(authorLogin) || string.IsNullOrEmpty(authorName))
            {
                return BadRequest(
                    $"'{Http.HeaderNames.AmsAuthor}', '{Http.HeaderNames.AmsAuthorLogin}' and '{Http.HeaderNames.AmsAuthorName}' " +
                    "request headers must be specified.");
            }

            try
            {
                var sessionId = Guid.NewGuid();
                await _sessionManagementService.Setup(sessionId, templateId, null, language, new AuthorInfo(author, authorLogin, authorName));

                Response.Headers[HeaderNames.ETag] = $"\"{sessionId}\"";
                var routeValues = new Dictionary<string, string> { { "api-version", apiVersion.ToString() }, { nameof(sessionId), sessionId.ToString() } };
                return CreatedAtAction(nameof(Get), routeValues, null);
            }
            catch (ObjectNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (SessionCannotBeCreatedException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        /// Create session for uploading file(-s) using specific version of template
        /// </summary>
        /// <param name="apiVersion">API version</param>
        /// <param name="author">Author identifier</param>
        /// <param name="authorLogin">Author login</param>
        /// <param name="authorName">Author name</param>
        /// <param name="language">Language of session</param>
        /// <param name="templateId">Template identifier</param>
        /// <param name="templateVersionId">Template version identifier</param>
        /// <returns>HTTP code</returns>
        [HttpPost("{language:lang}/{templateId:long}/{templateVersionId}")]
        [ProducesResponseType(201)]
        [ProducesResponseType(typeof(string), 400)]
        [ProducesResponseType(typeof(string), 404)]
        public async Task<IActionResult> SetupSession(
            ApiVersion apiVersion,
            [FromHeader(Name = Http.HeaderNames.AmsAuthor)] string author,
            [FromHeader(Name = Http.HeaderNames.AmsAuthorLogin)] string authorLogin,
            [FromHeader(Name = Http.HeaderNames.AmsAuthorName)] string authorName,
            Language language,
            long templateId,
            string templateVersionId)
        {
            if (string.IsNullOrEmpty(author) || string.IsNullOrEmpty(authorLogin) || string.IsNullOrEmpty(authorName))
            {
                return BadRequest(
                    $"'{Http.HeaderNames.AmsAuthor}', '{Http.HeaderNames.AmsAuthorLogin}' and '{Http.HeaderNames.AmsAuthorName}' " +
                    "request headers must be specified.");
            }

            try
            {
                var sessionId = Guid.NewGuid();
                await _sessionManagementService.Setup(sessionId, templateId, templateVersionId, language, new AuthorInfo(author, authorLogin, authorName));

                Response.Headers[HeaderNames.ETag] = $"\"{sessionId}\"";
                var routeValues = new Dictionary<string, string> { { "api-version", apiVersion.ToString() }, { nameof(sessionId), sessionId.ToString() } };
                return CreatedAtAction(nameof(Get), routeValues, null);
            }
            catch (ObjectNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (SessionCannotBeCreatedException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        /// Upload file
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="templateCode">Template code of element for uploading file</param>
        /// <param name="rawFileType">File type</param>
        /// <param name="rawImageSize">File size (for "sizeSpecificBitmapImage" file type)</param>
        /// <returns>Raw value of uploaded file</returns>
        [AllowAnonymous]
        [HttpPost("{sessionId:guid}/upload/{templateCode:int}")]
        [DisableFormValueModelBinding]
        [MultipartBodyLengthLimit]
        [ProducesResponseType(typeof(UploadedFileValue), 201)]
        [ProducesResponseType(typeof(string), 400)]
        [ProducesResponseType(typeof(string), 404)]
        [ProducesResponseType(typeof(string), 410)]
        [ProducesResponseType(typeof(object), 422)]
        [ProducesResponseType(typeof(string), 452)]
        public async Task<IActionResult> UploadFile(
            Guid sessionId,
            int templateCode,
            [FromHeader(Name = Http.HeaderNames.AmsFileType)] string rawFileType,
            [FromHeader(Name = Http.HeaderNames.AmsImageSize)] string rawImageSize)
        {
            var multipartBoundary = Request.GetMultipartBoundary();
            if (string.IsNullOrEmpty(multipartBoundary))
            {
                return BadRequest($"Expected a multipart request, but got '{Request.ContentType}'.");
            }

            MultipartUploadSession uploadSession = null;
            try
            {
                var formFeature = Request.HttpContext.Features.Get<IFormFeature>();
                var form = await formFeature.ReadFormAsync(CancellationToken.None);

                if (form.Files.Count != 1)
                {
                    return BadRequest("Request body must contain single file section.");
                }

                var file = form.Files.First();

                if (!TryParseUploadedFileMetadata(file, rawFileType, rawImageSize, out var uploadedFileMetadata, out var error))
                {
                    return BadRequest(error);
                }

                uploadSession = await _sessionManagementService.InitiateMultipartUpload(sessionId, templateCode, uploadedFileMetadata);
                _logger.LogInformation("Multipart upload for file '{fileName}' in session '{sessionId}' was initiated.", file.FileName, sessionId);

                using (var inputStream = file.OpenReadStream())
                {
                    await _sessionManagementService.UploadFilePart(uploadSession, inputStream, templateCode);
                }

                var uploadedFileKey = await _sessionManagementService.CompleteMultipartUpload(uploadSession);

                return Created(_cdnOptions.AsRawUri(uploadedFileKey), new UploadedFileValue(uploadedFileKey));
            }
            catch (ObjectNotFoundException)
            {
                return NotFound();
            }
            catch (SessionExpiredException ex)
            {
                return Gone(ex.ExpiredAt);
            }
            catch (InvalidTemplateException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (InvalidDataException ex)
            {
                return RequestTooLarge(ex.Message);
            }
            catch (MissingFilenameException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (InvalidBinaryException ex)
            {
                return Unprocessable(GenerateErrorJsonResult(ex));
            }
            finally
            {
                if (uploadSession != null)
                {
                    await _sessionManagementService.AbortMultipartUpload(uploadSession);
                }
            }
        }

        /// <summary>
        /// Fetch file from specified URL
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="templateCode">Template code of element for fetching file</param>
        /// <param name="fetchParameters">Fetch parameters</param>
        /// <returns>Raw value of fetched file</returns>
        [AllowAnonymous]
        [HttpPost("{sessionId:guid}/fetch/{templateCode:int}")]
        [Consumes(Http.ContentType.Json)]
        [ProducesResponseType(typeof(FetchedFileValue), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(string), StatusCodes.Status410Gone)]
        [ProducesResponseType(typeof(object), StatusCodes.Status422UnprocessableEntity)]
        [ProducesResponseType(typeof(string), StatusCodes.Status424FailedDependency)]
        public async Task<ActionResult> FetchFile(
            Guid sessionId,
            int templateCode,
            [FromBody] FetchParameters fetchParameters)
        {
            if (fetchParameters == null)
            {
                return BadRequest($"Request body with {nameof(fetchParameters)} must be specified");
            }

            try
            {
                var fetchedFileKey = await _sessionManagementService.FetchFile(sessionId, templateCode, fetchParameters);

                return Created(_cdnOptions.AsRawUri(fetchedFileKey), new FetchedFileValue(fetchedFileKey));
            }
            catch (MissingFilenameException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (InvalidTemplateException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (InvalidFetchUrlException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (ObjectNotFoundException)
            {
                return NotFound();
            }
            catch (SessionExpiredException ex)
            {
                return Gone(ex.ExpiredAt);
            }
            catch (FetchFailedException ex)
            {
                return FailedDependency(ex.Message);
            }
            catch (InvalidBinaryException ex)
            {
                return Unprocessable(GenerateErrorJsonResult(ex));
            }
        }

        private static bool TryParseUploadedFileMetadata(
            IFormFile file,
            string rawFileType,
            string rawImageSize,
            out IUploadedFileMetadata uploadedFileMetadata,
            out string error)
        {
            uploadedFileMetadata = null;
            error = null;

            if (string.IsNullOrEmpty(rawFileType))
            {
                uploadedFileMetadata = new GenericUploadedFileMetadata(FileType.NotSet, file.FileName, file.ContentType, file.Length);
                return true;
            }

            if (!Enum.TryParse<FileType>(rawFileType, true, out var fileType))
            {
                error = $"Cannot parse '{Http.HeaderNames.AmsFileType}' header value '{fileType}'";
                return false;
            }

            switch (fileType)
            {
                case FileType.SizeSpecificBitmapImage:
                    if (!ImageSize.TryParse(rawImageSize, out var imageSize))
                    {
                        error = $"Cannot parse '{Http.HeaderNames.AmsImageSize}' header value '{rawImageSize}'";
                        return false;
                    }

                    uploadedFileMetadata = new UploadedImageMetadata(FileType.SizeSpecificBitmapImage, file.FileName, file.ContentType, file.Length, imageSize);
                    return true;
                default:
                    error = $"Unexpected '{Http.HeaderNames.AmsFileType}' header value '{fileType}'";
                    return false;
            }
        }

        private static JToken GenerateErrorJsonResult(InvalidBinaryException ex) =>
            new JObject
                {
                    { Tokens.ErrorsToken, new JArray() },
                    { Tokens.ElementsToken, new JArray { ex.SerializeToJson() } }
                };

        private sealed class UploadedFileValue : IObjectElementRawValue
        {
            public UploadedFileValue(string raw)
            {
                Raw = raw;
            }

            public string Raw { get; }
        }

        private sealed class FetchedFileValue : IObjectElementRawValue
        {
            public FetchedFileValue(string raw)
            {
                Raw = raw;
            }

            public string Raw { get; }
        }
    }
}
