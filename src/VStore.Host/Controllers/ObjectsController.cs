using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

using NuClear.VStore.DataContract;
using NuClear.VStore.Descriptors.Objects;
using NuClear.VStore.Descriptors.Templates;
using NuClear.VStore.Http.Core.Controllers;
using NuClear.VStore.Http.Core.Extensions;
using NuClear.VStore.Locks;
using NuClear.VStore.Objects;
using NuClear.VStore.Objects.ContentValidation;
using NuClear.VStore.S3;

namespace NuClear.VStore.Host.Controllers
{
    [ApiController]
    [ApiVersion("1.0")]
    [Route("api/{api-version:apiVersion}/objects")]
    public sealed class ObjectsController : VStoreController
    {
        private readonly IObjectsStorageReader _objectsStorageReader;
        private readonly IObjectsManagementService _objectsManagementService;
        private readonly ILogger<ObjectsController> _logger;

        public ObjectsController(
            IObjectsStorageReader objectsStorageReader,
            IObjectsManagementService objectsManagementService,
            ILogger<ObjectsController> logger)
        {
            _logger = logger;
            _objectsStorageReader = objectsStorageReader;
            _objectsManagementService = objectsManagementService;
        }

        /// <summary>
        /// Get all objects
        /// </summary>
        /// <param name="continuationToken">Token to continue reading list, should be empty on initial call</param>
        /// <returns>List of object descriptors</returns>
        [HttpGet]
        [ProducesResponseType(200)]
        public async Task<ActionResult<IReadOnlyCollection<IdentifyableObjectRecord<long>>>> List([FromHeader(Name = Http.HeaderNames.AmsContinuationToken)]string continuationToken)
        {
            var container = await _objectsStorageReader.List(continuationToken?.Trim('"'));

            if (!string.IsNullOrEmpty(container.ContinuationToken))
            {
                Response.Headers[Http.HeaderNames.AmsContinuationToken] = $"\"{container.ContinuationToken}\"";
            }

            return Json(container.Collection);
        }

        /// <summary>
        /// Get specified objects
        /// </summary>
        /// <param name="ids">Object identifiers</param>
        /// <returns>List of object descriptors</returns>
        [HttpGet("specified")]
        [ProducesResponseType(200)]
        public async Task<ActionResult<IReadOnlyCollection<ObjectMetadataRecord>>> List(IReadOnlyCollection<long> ids)
        {
            var records = await _objectsStorageReader.GetObjectMetadatas(ids);
            return Json(records);
        }

        /// <summary>
        /// Get object's specific version template
        /// </summary>
        /// <param name="id">Object identifier</param>
        /// <param name="versionId">Object version</param>
        /// <returns>Template descriptor</returns>
        [HttpGet("{id:long}/{versionId}/template")]
        [ResponseCache(Duration = 120)]
        [ProducesResponseType(200)]
        [ProducesResponseType(404)]
        public async Task<ActionResult<IVersionedTemplateDescriptor>> GetTemplateDescriptor(long id, string versionId)
        {
            try
            {
                var templateDescriptor = await _objectsStorageReader.GetTemplateDescriptor(id, versionId);

                Response.Headers[HeaderNames.ETag] = $"\"{templateDescriptor.VersionId}\"";
                Response.Headers[HeaderNames.LastModified] = templateDescriptor.LastModified.ToString("R");
                return Json(templateDescriptor);
            }
            catch (ObjectNotFoundException)
            {
                return NotFound();
            }
        }

        /// <summary>
        /// Get object versions metadata
        /// </summary>
        /// <param name="id">Object identifier</param>
        /// <returns>List of object versions</returns>
        [HttpGet("{id:long}/versions")]
        [ProducesResponseType(typeof(IReadOnlyCollection<ObjectVersionMetadataRecord>), 200)]
        [ProducesResponseType(404)]
        [ProducesResponseType(503)]
        public async Task<ActionResult<IReadOnlyCollection<ObjectVersionRecord>>> GetVersions(long id)
        {
            try
            {
                var versions = await _objectsStorageReader.GetObjectVersionsMetadata(id, initialVersionId: null);
                return Json(versions);
            }
            catch (ObjectNotFoundException)
            {
                return NotFound();
            }
            catch (LockAlreadyExistsException)
            {
                return ServiceUnavailable("Simultaneous object versions listing and its creation/modification");
            }
        }

        /// <summary>
        /// Get object's latest version metadata
        /// </summary>
        /// <param name="id">Object identifier</param>
        /// <param name="ifNoneMatch">Object version to check if it has been modified (optional)</param>
        /// <returns>No body with status 200 Ok or 404 Not Found or 304 Not Modified</returns>
        [HttpHead("{id:long}")]
        [ProducesResponseType(200)]
        [ProducesResponseType(304)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> GetMetadata(long id, [FromHeader(Name = HeaderNames.IfNoneMatch)] string ifNoneMatch)
        {
            var latestVersion = await _objectsStorageReader.GetObjectLatestVersion(id);
            if (latestVersion == null)
            {
                return NotFound();
            }

            Response.Headers[HeaderNames.ETag] = $"\"{latestVersion.VersionId}\"";
            Response.Headers[HeaderNames.LastModified] = latestVersion.LastModified.ToString("R");

            if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch.Trim('"') == latestVersion.VersionId)
            {
                return NotModified();
            }

            return Ok();
        }

        /// <summary>
        /// Get object's latest version
        /// </summary>
        /// <param name="id">Object identifier</param>
        /// <param name="ifNoneMatch">Object version to check if it has been modified (optional)</param>
        /// <returns>Object descriptor or 304 Not Modified</returns>
        [HttpGet("{id:long}")]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(304)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Get(long id, [FromHeader(Name = HeaderNames.IfNoneMatch)] string ifNoneMatch)
        {
            try
            {
                var objectDescriptor = await _objectsStorageReader.GetObjectDescriptor(id, null, CancellationToken.None);

                Response.Headers[HeaderNames.ETag] = $"\"{objectDescriptor.VersionId}\"";
                Response.Headers[HeaderNames.LastModified] = objectDescriptor.LastModified.ToString("R");

                if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch.Trim('"') == objectDescriptor.VersionId)
                {
                    return NotModified();
                }

                return Json(
                    new
                    {
                        objectDescriptor.Id,
                        objectDescriptor.VersionId,
                        objectDescriptor.LastModified,
                        objectDescriptor.TemplateId,
                        objectDescriptor.TemplateVersionId,
                        objectDescriptor.Language,
                        objectDescriptor.Metadata.Author,
                        objectDescriptor.Metadata.AuthorLogin,
                        objectDescriptor.Metadata.AuthorName,
                        objectDescriptor.Properties,
                        objectDescriptor.Elements
                    });
            }
            catch (ObjectNotFoundException)
            {
                return NotFound();
            }
        }

        /// <summary>
        /// Get object's specific version metadata
        /// </summary>
        /// <param name="id">Object identifier</param>
        /// <param name="versionId">Object version</param>
        /// <returns>No body with status 200 Ok or 404 Not Found</returns>
        [HttpHead("{id:long}/{versionId}")]
        [ResponseCache(Duration = 120)]
        [ProducesResponseType(200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> GetVersionMetadata(long id, string versionId)
        {
            try
            {
                var lastModified = await _objectsStorageReader.GetObjectVersionLastModified(id, versionId);
                Response.Headers[HeaderNames.ETag] = $"\"{versionId}\"";
                Response.Headers[HeaderNames.LastModified] = lastModified.ToString("R");
                return Ok();
            }
            catch (ObjectNotFoundException)
            {
                return NotFound();
            }
        }

        /// <summary>
        /// Get object's specific version
        /// </summary>
        /// <param name="id">Object identifier</param>
        /// <param name="versionId">Object version</param>
        /// <returns>Object descriptor</returns>
        [HttpGet("{id:long}/{versionId}")]
        [ResponseCache(Duration = 120)]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> GetVersion(long id, string versionId)
        {
            try
            {
                var objectDescriptor = await _objectsStorageReader.GetObjectDescriptor(id, versionId, CancellationToken.None);

                Response.Headers[HeaderNames.ETag] = $"\"{objectDescriptor.VersionId}\"";
                Response.Headers[HeaderNames.LastModified] = objectDescriptor.LastModified.ToString("R");
                return Json(
                    new
                    {
                        objectDescriptor.Id,
                        objectDescriptor.VersionId,
                        objectDescriptor.LastModified,
                        objectDescriptor.TemplateId,
                        objectDescriptor.TemplateVersionId,
                        objectDescriptor.Language,
                        objectDescriptor.Metadata.Author,
                        objectDescriptor.Metadata.AuthorLogin,
                        objectDescriptor.Metadata.AuthorName,
                        objectDescriptor.Properties,
                        objectDescriptor.Elements
                    });
            }
            catch (ObjectNotFoundException)
            {
                return NotFound();
            }
        }

        /// <summary>
        /// Create new object
        /// </summary>
        /// <param name="id">Object identifier</param>
        /// <param name="author">Author identifier</param>
        /// <param name="authorLogin">Author login</param>
        /// <param name="authorName">Author name</param>
        /// <param name="objectDescriptor">JSON with object descriptor</param>
        /// <returns>HTTP code</returns>
        [HttpPost("{id:long}")]
        [ProducesResponseType(201)]
        [ProducesResponseType(typeof(string), 400)]
        [ProducesResponseType(404)]
        [ProducesResponseType(409)]
        [ProducesResponseType(typeof(object), 422)]
        [ProducesResponseType(423)]
        public async Task<IActionResult> Create(
            long id,
            [FromHeader(Name = Http.HeaderNames.AmsAuthor)] string author,
            [FromHeader(Name = Http.HeaderNames.AmsAuthorLogin)] string authorLogin,
            [FromHeader(Name = Http.HeaderNames.AmsAuthorName)] string authorName,
            [FromBody] IObjectDescriptor objectDescriptor)
        {
            if (string.IsNullOrEmpty(author) || string.IsNullOrEmpty(authorLogin) || string.IsNullOrEmpty(authorName))
            {
                return BadRequest(
                    $"'{Http.HeaderNames.AmsAuthor}', '{Http.HeaderNames.AmsAuthorLogin}' and '{Http.HeaderNames.AmsAuthorName}' " +
                    "request headers must be specified.");
            }

            if (TryGetModelErrors(out var errors))
            {
                return BadRequest(errors);
            }

            try
            {
                var versionId = await _objectsManagementService.Create(id, new AuthorInfo(author, authorLogin, authorName), objectDescriptor);
                var url = Url.AbsoluteAction("GetVersion", "Objects", new { id, versionId });

                Response.Headers[HeaderNames.ETag] = $"\"{versionId}\"";
                return Created(url, null);
            }
            catch (InvalidObjectException ex)
            {
                return Unprocessable(ex.SerializeToJson());
            }
            catch (ObjectNotFoundException ex)
            {
                _logger.LogError(default, ex, "Error occured while creating object");
                return Unprocessable(ex.Message);
            }
            catch (ObjectAlreadyExistsException)
            {
                return Conflict("Object with specified id already exists");
            }
            catch (LockAlreadyExistsException)
            {
                return Locked("Simultaneous creation of object with the same id");
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (ObjectInconsistentException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        /// Modify existing object
        /// </summary>
        /// <param name="id">Object identifier</param>
        /// <param name="ifMatch">Object version (should be latest version)</param>
        /// <param name="author">Author identifier</param>
        /// <param name="authorLogin">Author login</param>
        /// <param name="authorName">Author name</param>
        /// <param name="objectDescriptor">JSON with object descriptor</param>
        /// <returns>HTTP code</returns>
        [HttpPatch("{id:long}")]
        [ProducesResponseType(204)]
        [ProducesResponseType(typeof(string), 400)]
        [ProducesResponseType(404)]
        [ProducesResponseType(412)]
        [ProducesResponseType(typeof(object), 422)]
        [ProducesResponseType(423)]
        public async Task<IActionResult> Modify(
            long id,
            [FromHeader(Name = HeaderNames.IfMatch)] string ifMatch,
            [FromHeader(Name = Http.HeaderNames.AmsAuthor)] string author,
            [FromHeader(Name = Http.HeaderNames.AmsAuthorLogin)] string authorLogin,
            [FromHeader(Name = Http.HeaderNames.AmsAuthorName)] string authorName,
            [FromBody] IObjectDescriptor objectDescriptor)
        {
            if (string.IsNullOrEmpty(ifMatch))
            {
                return BadRequest($"'{HeaderNames.IfMatch}' request header must be specified.");
            }

            if (string.IsNullOrEmpty(author) || string.IsNullOrEmpty(authorLogin) || string.IsNullOrEmpty(authorName))
            {
                return BadRequest(
                    $"'{Http.HeaderNames.AmsAuthor}', '{Http.HeaderNames.AmsAuthorLogin}' and '{Http.HeaderNames.AmsAuthorName}' " +
                    "request headers must be specified.");
            }

            if (TryGetModelErrors(out var errors))
            {
                return BadRequest(errors);
            }

            try
            {
                var latestVersionId = await _objectsManagementService.Modify(
                                          id,
                                          ifMatch.Trim('"'),
                                          new AuthorInfo(author, authorLogin, authorName),
                                          objectDescriptor);
                var url = Url.AbsoluteAction("GetVersion", "Objects", new { id, versionId = latestVersionId });

                Response.Headers[HeaderNames.ETag] = $"\"{latestVersionId}\"";
                return NoContent(url);
            }
            catch (InvalidObjectException ex)
            {
                return Unprocessable(ex.SerializeToJson());
            }
            catch (ObjectNotFoundException ex)
            {
                return NotFound(ex.Message);
            }
            catch (LockAlreadyExistsException)
            {
                return Locked("Simultaneous modification of object");
            }
            catch (ConcurrencyException)
            {
                return PreconditionFailed();
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (ObjectInconsistentException ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }
}
