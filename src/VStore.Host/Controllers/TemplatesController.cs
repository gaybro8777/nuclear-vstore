using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

using Newtonsoft.Json.Linq;

using NuClear.VStore.DataContract;
using NuClear.VStore.Descriptors.Templates;
using NuClear.VStore.Http.Core.Controllers;
using NuClear.VStore.Http.Core.Extensions;
using NuClear.VStore.Json;
using NuClear.VStore.Locks;
using NuClear.VStore.Objects;
using NuClear.VStore.S3;
using NuClear.VStore.Templates;

namespace NuClear.VStore.Host.Controllers
{
    [ApiController]
    [ApiVersion("1.2")]
    [ApiVersion("1.1", Deprecated = true)]
    [ApiVersion("1.0", Deprecated = true)]
    [Route("api/{api-version:apiVersion}/templates")]
    public class TemplatesController : VStoreController
    {
        private readonly ITemplatesStorageReader _templatesStorageReader;
        private readonly TemplatesManagementService _templatesManagementService;

        public TemplatesController(ITemplatesStorageReader templatesStorageReader, TemplatesManagementService templatesManagementService)
        {
            _templatesStorageReader = templatesStorageReader;
            _templatesManagementService = templatesManagementService;
        }

        /// <summary>
        /// Get available template element descriptors
        /// </summary>
        /// <returns>List of template element descriptors</returns>
        [HttpGet("element-descriptors/available")]
        [ProducesResponseType(typeof(IReadOnlyCollection<IElementDescriptor>), 200)]
        public IActionResult GetAvailableElementDescriptors() => Json(_templatesManagementService.GetAvailableElementDescriptors());

        /// <summary>
        /// Get all templates
        /// </summary>
        /// <param name="continuationToken">Token to continue reading list, should be empty on initial call</param>
        /// <returns>List of template descriptors</returns>
        [HttpGet]
        [ProducesResponseType(typeof(IReadOnlyCollection<IdentifyableObjectRecord<long>>), 200)]
        public async Task<IActionResult> List([FromHeader(Name = Http.HeaderNames.AmsContinuationToken)]string continuationToken)
        {
            var container = await _templatesStorageReader.List(continuationToken?.Trim('"'));

            if (!string.IsNullOrEmpty(container.ContinuationToken))
            {
                Response.Headers[Http.HeaderNames.AmsContinuationToken] = $"\"{container.ContinuationToken}\"";
            }

            return Json(container.Collection);
        }

        /// <summary>
        /// Get specified templates
        /// </summary>
        /// <param name="ids">List of template identifiers</param>
        /// <returns>List of specified template descriptors</returns>
        [HttpGet("specified")]
        [ProducesResponseType(typeof(IReadOnlyCollection<ObjectMetadataRecord>), 200)]
        public async Task<IActionResult> List(IReadOnlyCollection<long> ids)
        {
            var records = await _templatesStorageReader.GetTemplateMetadatas(ids);
            return Json(records);
        }

        /// <summary>
        /// Get last version of specified template
        /// </summary>
        /// <param name="id">Template identifier</param>
        /// <param name="ifNoneMatch">Version of template to check if it has been modified (optional)</param>
        /// <returns>Template descriptor or 304 Not Modified</returns>
        [HttpGet("{id:long}")]
        [ResponseCache(Duration = 120)]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(304)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Get(long id, [FromHeader(Name = HeaderNames.IfNoneMatch)] string ifNoneMatch)
        {
            try
            {
                var templateDescriptor = await _templatesStorageReader.GetTemplateDescriptor(id, null);

                Response.Headers[HeaderNames.ETag] = $"\"{templateDescriptor.VersionId}\"";
                Response.Headers[HeaderNames.LastModified] = templateDescriptor.LastModified.ToString("R");

                if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch.Trim('"') == templateDescriptor.VersionId)
                {
                    return NotModified();
                }

                return Json(
                    new
                        {
                            id,
                            templateDescriptor.VersionId,
                            templateDescriptor.LastModified,
                            templateDescriptor.Author,
                            templateDescriptor.AuthorLogin,
                            templateDescriptor.AuthorName,
                            templateDescriptor.Properties,
                            templateDescriptor.Elements
                        });
            }
            catch (ObjectNotFoundException)
            {
                return NotFound();
            }
        }

        /// <summary>
        /// Get specific version of template
        /// </summary>
        /// <param name="id">Template identifier</param>
        /// <param name="versionId">Version</param>
        /// <returns>Template descriptor</returns>
        [HttpGet("{id:long}/{versionId}")]
        [ResponseCache(Duration = 120)]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> GetVersion(long id, string versionId)
        {
            try
            {
                var templateDescriptor = await _templatesStorageReader.GetTemplateDescriptor(id, versionId);

                Response.Headers[HeaderNames.ETag] = $"\"{templateDescriptor.VersionId}\"";
                Response.Headers[HeaderNames.LastModified] = templateDescriptor.LastModified.ToString("R");
                return Json(
                    new
                        {
                            id,
                            templateDescriptor.VersionId,
                            templateDescriptor.LastModified,
                            templateDescriptor.Author,
                            templateDescriptor.AuthorLogin,
                            templateDescriptor.AuthorName,
                            templateDescriptor.Properties,
                            templateDescriptor.Elements
                        });
            }
            catch (ObjectNotFoundException)
            {
                return NotFound();
            }
        }

        /// <summary>
        /// Get template versions
        /// </summary>
        /// <param name="id">Template identifier</param>
        /// <returns>List of template versions</returns>
        [MapToApiVersion("1.2")]
        [HttpGet("{id:long}/versions")]
        [ProducesResponseType(typeof(IReadOnlyCollection<TemplateVersionRecord>), 200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> GetVersions(long id)
        {
            try
            {
                var versions = await _templatesStorageReader.GetTemplateVersions(id);
                return Json(versions);
            }
            catch (ObjectNotFoundException)
            {
                return NotFound();
            }
        }

        /// <summary>
        /// Validate template elements (old API)
        /// </summary>
        /// <param name="elementDescriptors">Template element descriptors to validate</param>
        /// <returns>Validation errors or 200 Ok</returns>
        [Obsolete, MapToApiVersion("1.0")]
        [HttpPost("validate-elements")]
        [ProducesResponseType(200)]
        [ProducesResponseType(typeof(object), 422)]
        public async Task<IActionResult> ValidateElementsV10([FromBody] IReadOnlyCollection<IElementDescriptor> elementDescriptors)
        {
            try
            {
                await _templatesManagementService.VerifyElementDescriptorsConsistency(elementDescriptors);
                return Ok();
            }
            catch (TemplateValidationException ex)
            {
                return Unprocessable(GenerateTemplateErrorJsonV10(ex));
            }
        }

        /// <summary>
        /// Validate template elements
        /// </summary>
        /// <param name="elementDescriptors">Template element descriptors to validate</param>
        /// <returns>Validation errors or 200 Ok</returns>
        [MapToApiVersion("1.1")]
        [MapToApiVersion("1.2")]
        [HttpPost("validate-elements")]
        [ProducesResponseType(200)]
        [ProducesResponseType(typeof(object), 422)]
        public async Task<IActionResult> ValidateElements([FromBody] IReadOnlyCollection<IElementDescriptor> elementDescriptors)
        {
            try
            {
                await _templatesManagementService.VerifyElementDescriptorsConsistency(elementDescriptors);
                return Ok();
            }
            catch (TemplateValidationException ex)
            {
                return Unprocessable(GenerateTemplateErrorJson(ex));
            }
        }

        /// <summary>
        /// Create new template (old API)
        /// </summary>
        /// <param name="id">Template identifier</param>
        /// <param name="author">Author identifier</param>
        /// <param name="authorLogin">Author login</param>
        /// <param name="authorName">Author name</param>
        /// <param name="templateDescriptor">JSON with template descriptor</param>
        /// <returns>HTTP code</returns>
        [Obsolete, MapToApiVersion("1.0")]
        [HttpPost("{id:long}")]
        [ProducesResponseType(201)]
        [ProducesResponseType(typeof(object), 400)]
        [ProducesResponseType(409)]
        [ProducesResponseType(typeof(object), 422)]
        [ProducesResponseType(423)]
        public async Task<IActionResult> CreateV10(
            long id,
            [FromHeader(Name = Http.HeaderNames.AmsAuthor)] string author,
            [FromHeader(Name = Http.HeaderNames.AmsAuthorLogin)] string authorLogin,
            [FromHeader(Name = Http.HeaderNames.AmsAuthorName)] string authorName,
            [FromBody] ITemplateDescriptor templateDescriptor)
        {
            return await CreateInternal(id, author, authorLogin, authorName, templateDescriptor, GenerateTemplateErrorJsonV10);
        }

        /// <summary>
        /// Create new template
        /// </summary>
        /// <param name="id">Template identifier</param>
        /// <param name="author">Author identifier</param>
        /// <param name="authorLogin">Author login</param>
        /// <param name="authorName">Author name</param>
        /// <param name="templateDescriptor">JSON with template descriptor</param>
        /// <returns>HTTP code</returns>
        [MapToApiVersion("1.1")]
        [MapToApiVersion("1.2")]
        [HttpPost("{id:long}")]
        [ProducesResponseType(201)]
        [ProducesResponseType(typeof(object), 400)]
        [ProducesResponseType(409)]
        [ProducesResponseType(typeof(object), 422)]
        [ProducesResponseType(423)]
        public async Task<IActionResult> Create(
            long id,
            [FromHeader(Name = Http.HeaderNames.AmsAuthor)] string author,
            [FromHeader(Name = Http.HeaderNames.AmsAuthorLogin)] string authorLogin,
            [FromHeader(Name = Http.HeaderNames.AmsAuthorName)] string authorName,
            [FromBody] ITemplateDescriptor templateDescriptor)
        {
            return await CreateInternal(id, author, authorLogin, authorName, templateDescriptor, GenerateTemplateErrorJson);
        }

        /// <summary>
        /// Modify existing template (old API)
        /// </summary>
        /// <param name="id">Template identifier</param>
        /// <param name="ifMatch">Version of template to be modified (should be last version)</param>
        /// <param name="author">Author identifier</param>
        /// <param name="authorLogin">Author login</param>
        /// <param name="authorName">Author name</param>
        /// <param name="templateDescriptor">JSON with template descriptor</param>
        /// <returns>HTTP code</returns>
        [Obsolete, MapToApiVersion("1.0")]
        [HttpPut("{id:long}")]
        [ProducesResponseType(204)]
        [ProducesResponseType(typeof(string), 400)]
        [ProducesResponseType(404)]
        [ProducesResponseType(412)]
        [ProducesResponseType(typeof(object), 422)]
        [ProducesResponseType(423)]
        public async Task<IActionResult> ModifyV10(
            long id,
            [FromHeader(Name = HeaderNames.IfMatch)] string ifMatch,
            [FromHeader(Name = Http.HeaderNames.AmsAuthor)] string author,
            [FromHeader(Name = Http.HeaderNames.AmsAuthorLogin)] string authorLogin,
            [FromHeader(Name = Http.HeaderNames.AmsAuthorName)] string authorName,
            [FromBody] ITemplateDescriptor templateDescriptor)
        {
            return await ModifyInternal(id, ifMatch, author, authorLogin, authorName, templateDescriptor, GenerateTemplateErrorJsonV10);
        }

        /// <summary>
        /// Modify existing template
        /// </summary>
        /// <param name="id">Template identifier</param>
        /// <param name="ifMatch">Version of template to be modified (should be last version)</param>
        /// <param name="author">Author identifier</param>
        /// <param name="authorLogin">Author login</param>
        /// <param name="authorName">Author name</param>
        /// <param name="templateDescriptor">JSON with template descriptor</param>
        /// <returns>HTTP code</returns>
        [MapToApiVersion("1.1")]
        [MapToApiVersion("1.2")]
        [HttpPut("{id:long}")]
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
            [FromBody] ITemplateDescriptor templateDescriptor)
        {
            return await ModifyInternal(id, ifMatch, author, authorLogin, authorName, templateDescriptor, GenerateTemplateErrorJson);
        }

        private static JToken GenerateTemplateErrorJsonV10(TemplateValidationException ex) => new JArray { ex.SerializeToJsonV10() };

        private static JToken GenerateTemplateErrorJson(TemplateValidationException ex) =>
            new JObject
                {
                    { Tokens.ErrorsToken, new JArray() },
                    { Tokens.ElementsToken, new JArray { ex.SerializeToJson() } }
                };

        private async Task<IActionResult> CreateInternal(
            long id,
            string author,
            string authorLogin,
            string authorName,
            ITemplateDescriptor templateDescriptor,
            Func<TemplateValidationException, JToken> errorGenerator)
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
                var versionId = await _templatesManagementService.CreateTemplate(id, new AuthorInfo(author, authorLogin, authorName), templateDescriptor);
                var url = Url.AbsoluteAction("GetVersion", "Templates", new { id, versionId });

                Response.Headers[HeaderNames.ETag] = $"\"{versionId}\"";
                return Created(url, null);
            }
            catch (ObjectAlreadyExistsException)
            {
                return Conflict("Template with the same id already exists");
            }
            catch (LockAlreadyExistsException)
            {
                return Locked("Simultaneous creation of template with the same id");
            }
            catch (TemplateValidationException ex)
            {
                return Unprocessable(errorGenerator(ex));
            }
        }

        private async Task<IActionResult> ModifyInternal(
            long id,
            string ifMatch,
            string author,
            string authorLogin,
            string authorName,
            ITemplateDescriptor templateDescriptor,
            Func<TemplateValidationException, JToken> errorGenerator)
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
                var latestVersionId = await _templatesManagementService.ModifyTemplate(
                                          id,
                                          ifMatch.Trim('"'),
                                          new AuthorInfo(author, authorLogin, authorName),
                                          templateDescriptor);
                var url = Url.AbsoluteAction("GetVersion", "Templates", new { id, versionId = latestVersionId });

                Response.Headers[HeaderNames.ETag] = $"\"{latestVersionId}\"";
                return NoContent(url);
            }
            catch (ObjectNotFoundException)
            {
                return NotFound();
            }
            catch (TemplateValidationException ex)
            {
                return Unprocessable(errorGenerator(ex));
            }
            catch (LockAlreadyExistsException)
            {
                return Locked("Simultaneous modification of template");
            }
            catch (ConcurrencyException)
            {
                return PreconditionFailed();
            }
        }
    }
}
