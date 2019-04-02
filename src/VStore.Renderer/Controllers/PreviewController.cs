using System;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc;

using Newtonsoft.Json.Linq;

using NuClear.VStore.Descriptors.Objects;
using NuClear.VStore.Http.Core.Controllers;
using NuClear.VStore.ImageRendering;
using NuClear.VStore.Json;
using NuClear.VStore.Objects;
using NuClear.VStore.Options;
using NuClear.VStore.S3;
using NuClear.VStore.Sessions;

namespace NuClear.VStore.Renderer.Controllers
{
    [ApiVersion("2.0")]
    [ApiVersion("1.0", Deprecated = true)]
    [Route("previews")]
    public sealed class PreviewController : VStoreController
    {
        private readonly TimeSpan _retryAfter;
        private readonly RawFileStorageInfoProvider _rawFileStorageInfoProvider;
        private readonly IObjectsStorageReader _objectsStorageReader;
        private readonly ImagePreviewService _imagePreviewService;

        public PreviewController(
            ThrottlingOptions throttlingOptions,
            RawFileStorageInfoProvider rawFileStorageInfoProvider,
            IObjectsStorageReader objectsStorageReader,
            ImagePreviewService imagePreviewService)
        {
            _retryAfter = throttlingOptions.RetryAfter;
            _rawFileStorageInfoProvider = rawFileStorageInfoProvider;
            _objectsStorageReader = objectsStorageReader;
            _imagePreviewService = imagePreviewService;
        }

        /// <summary>
        /// Get composite image preview
        /// </summary>
        /// <param name="id">Object identifier</param>
        /// <param name="versionId">Object version</param>
        /// <param name="templateCode">Template code of object's element</param>
        /// <param name="width">Required width</param>
        /// <param name="height">Required height</param>
        /// <returns>File with preview</returns>
        [MapToApiVersion("2.0")]
        [HttpGet("{id:long}/{versionId}/{templateCode:int}/image_{width:int}x{height:int}.png")]
        [ProducesResponseType(typeof(byte[]), 200)]
        [ProducesResponseType(302)]
        [ProducesResponseType(typeof(string), 400)]
        [ProducesResponseType(404)]
        [ProducesResponseType(typeof(object), 422)]
        [ProducesResponseType(429)]
        public async Task<IActionResult> GetCompositeImagePreview(long id, string versionId, int templateCode, int width, int height)
        {
            if (width < 1 || height < 1)
            {
                return BadRequest("Incorrect width or height");
            }

            try
            {
                var imageElementValue = await _objectsStorageReader.GetImageElementValue(id, versionId, templateCode);
                if (imageElementValue.TryGetSizeSpecificBitmapImageRawValue(width, height, out var rawValue))
                {
                    return Redirect(_rawFileStorageInfoProvider.GetRawFileUrl(rawValue));
                }

                var (imageStream, contentType) = await _imagePreviewService.GetCroppedPreview(imageElementValue, templateCode, width, height);
                return new FileStreamResult(imageStream, contentType);
            }
            catch (ObjectNotFoundException)
            {
                return NotFound();
            }
            catch (OperationCanceledException)
            {
                return TooManyRequests(_retryAfter);
            }
            catch (MemoryLimitedException)
            {
                return TooManyRequests(_retryAfter);
            }
            catch (ObjectElementInvalidTypeException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (InvalidBinaryException ex)
            {
                return Unprocessable(GenerateErrorJsonResult(ex));
            }
        }

        /// <summary>
        /// Get composite image preview with rounded corners (old API)
        /// </summary>
        /// <param name="id">Object identifier</param>
        /// <param name="versionId">Object version</param>
        /// <param name="templateCode">Template code of object's element</param>
        /// <param name="width">Required width</param>
        /// <param name="height">Required height</param>
        /// <returns>File with preview</returns>
        [Obsolete, MapToApiVersion("1.0")]
        [HttpGet("{id:long}/{versionId}/{templateCode:int}/image_{width:int}x{height:int}.png")]
        [ProducesResponseType(typeof(byte[]), 200)]
        [ProducesResponseType(302)]
        [ProducesResponseType(typeof(string), 400)]
        [ProducesResponseType(404)]
        [ProducesResponseType(typeof(object), 422)]
        [ProducesResponseType(429)]
        public async Task<IActionResult> GetCompositeImagePreviewV10(long id, string versionId, int templateCode, int width, int height)
        {
            if (width < 1 || height < 1)
            {
                return BadRequest("Incorrect width or height");
            }

            try
            {
                var imageElementValue = await _objectsStorageReader.GetImageElementValue(id, versionId, templateCode);
                if (imageElementValue.TryGetSizeSpecificBitmapImageRawValue(width, height, out var rawValue))
                {
                    return Redirect(_rawFileStorageInfoProvider.GetRawFileUrl(rawValue));
                }

                var (imageStream, contentType) = await _imagePreviewService.GetCroppedAndRoundedPreview(imageElementValue, templateCode, width, height);
                return new FileStreamResult(imageStream, contentType);
            }
            catch (ObjectNotFoundException)
            {
                return NotFound();
            }
            catch (OperationCanceledException)
            {
                return TooManyRequests(_retryAfter);
            }
            catch (MemoryLimitedException)
            {
                return TooManyRequests(_retryAfter);
            }
            catch (ObjectElementInvalidTypeException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (InvalidBinaryException ex)
            {
                return Unprocessable(GenerateErrorJsonResult(ex));
            }
        }

        /// <summary>
        /// Get scalable image preview
        /// </summary>
        /// <param name="id">Object identifier</param>
        /// <param name="versionId">Object version</param>
        /// <param name="templateCode">Template code of object's element</param>
        /// <param name="width">Required width</param>
        /// <param name="height">Required height</param>
        /// <returns>File with preview</returns>
        [HttpGet("{id:long}/{versionId}/{templateCode:int}/{width:int}x{height:int}")]
        [ProducesResponseType(typeof(byte[]), 200)]
        [ProducesResponseType(typeof(string), 400)]
        [ProducesResponseType(404)]
        [ProducesResponseType(typeof(object), 422)]
        [ProducesResponseType(429)]
        public async Task<IActionResult> GetScaledImagePreview(long id, string versionId, int templateCode, int width, int height)
        {
            if (width < 1 || height < 1)
            {
                return BadRequest("Incorrect width or height");
            }

            try
            {
                var imageElementValue = await _objectsStorageReader.GetImageElementValue(id, versionId, templateCode);
                var (imageStream, contentType) = await _imagePreviewService.GetScaledPreview(imageElementValue, templateCode, width, height);
                return new FileStreamResult(imageStream, contentType);
            }
            catch (ObjectNotFoundException)
            {
                return NotFound();
            }
            catch (OperationCanceledException)
            {
                return TooManyRequests(_retryAfter);
            }
            catch (MemoryLimitedException)
            {
                return TooManyRequests(_retryAfter);
            }
            catch (ObjectElementInvalidTypeException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (InvalidBinaryException ex)
            {
                return Unprocessable(GenerateErrorJsonResult(ex));
            }
        }

        private static JToken GenerateErrorJsonResult(InvalidBinaryException ex) =>
            new JObject
                {
                    { Tokens.ErrorsToken, new JArray() },
                    { Tokens.ElementsToken, new JArray { ex.SerializeToJson() } }
                };
    }
}