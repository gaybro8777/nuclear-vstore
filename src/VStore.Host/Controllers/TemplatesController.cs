﻿using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

using Newtonsoft.Json.Linq;

using NuClear.VStore.Descriptors;
using NuClear.VStore.Descriptors.Templates;
using NuClear.VStore.Host.Extensions;
using NuClear.VStore.Locks;
using NuClear.VStore.Objects;
using NuClear.VStore.S3;
using NuClear.VStore.Templates;

namespace NuClear.VStore.Host.Controllers
{
    [ApiVersion("1.0")]
    [Route("api/{api-version:apiVersion}/templates")]
    public class TemplatesController : VStoreController
    {
        private readonly TemplatesStorageReader _templatesStorageReader;
        private readonly TemplatesManagementService _templatesManagementService;

        public TemplatesController(TemplatesStorageReader templatesStorageReader, TemplatesManagementService templatesManagementService)
        {
            _templatesStorageReader = templatesStorageReader;
            _templatesManagementService = templatesManagementService;
        }

        [HttpGet("element-descriptors/available")]
        [ProducesResponseType(typeof(IReadOnlyCollection<IElementDescriptor>), 200)]
        public IActionResult GetAvailableElementDescriptors()
        {
            return Json(_templatesManagementService.GetAvailableElementDescriptors());
        }

        [HttpGet]
        [ProducesResponseType(typeof(IReadOnlyCollection<IdentifyableObjectDescriptor<long>>), 200)]
        public async Task<IActionResult> List([FromHeader(Name = Headers.HeaderNames.AmsContinuationToken)]string continuationToken)
        {
            var container = await _templatesStorageReader.GetTemplateMetadatas(continuationToken?.Trim('"'));

            if (!string.IsNullOrEmpty(container.ContinuationToken))
            {
                Response.Headers[Headers.HeaderNames.AmsContinuationToken] = $"\"{container.ContinuationToken}\"";
            }

            return Json(container.Collection);
        }

        [HttpGet("{id}")]
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
                            templateDescriptor.Properties,
                            templateDescriptor.Elements
                        });
            }
            catch (ObjectNotFoundException)
            {
                return NotFound();
            }
        }

        [HttpGet("{id}/{versionId}")]
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
                            templateDescriptor.Properties,
                            templateDescriptor.Elements
                        });
            }
            catch (ObjectNotFoundException)
            {
                return NotFound();
            }
        }

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

        [HttpPost("{id}/validate-elements")]
        [ProducesResponseType(200)]
        [ProducesResponseType(typeof(object), 422)]
        public async Task<IActionResult> ValidateElements(long id, [FromBody] IReadOnlyCollection<IElementDescriptor> elementDescriptors)
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

        [HttpPost("{id}")]
        [ProducesResponseType(201)]
        [ProducesResponseType(typeof(object), 400)]
        [ProducesResponseType(409)]
        [ProducesResponseType(typeof(object), 422)]
        public async Task<IActionResult> Create(
            long id,
            [FromHeader(Name = Headers.HeaderNames.AmsAuthor)] string author,
            [FromBody] ITemplateDescriptor templateDescriptor)
        {
            if (string.IsNullOrEmpty(author))
            {
                return BadRequest($"'{Headers.HeaderNames.AmsAuthor}' request header must be specified.");
            }

            if (templateDescriptor == null)
            {
                return BadRequest("Template descriptor must be set.");
            }

            try
            {
                var versionId = await _templatesManagementService.CreateTemplate(id, author, templateDescriptor);
                var url = Url.AbsoluteAction("GetVersion", "Templates", new { id, versionId });

                Response.Headers[HeaderNames.ETag] = $"\"{versionId}\"";
                return Created(url, null);
            }
            catch (ObjectAlreadyExistsException)
            {
                return Conflict();
            }
            catch (TemplateValidationException ex)
            {
                return Unprocessable(GenerateTemplateErrorJson(ex));
            }
        }

        [HttpPut("{id}")]
        [ProducesResponseType(204)]
        [ProducesResponseType(typeof(string), 400)]
        [ProducesResponseType(404)]
        [ProducesResponseType(409)]
        [ProducesResponseType(412)]
        [ProducesResponseType(typeof(object), 422)]
        public async Task<IActionResult> Modify(
            long id,
            [FromHeader(Name = HeaderNames.IfMatch)] string ifMatch,
            [FromHeader(Name = Headers.HeaderNames.AmsAuthor)] string author,
            [FromBody] ITemplateDescriptor templateDescriptor)
        {
            if (string.IsNullOrEmpty(ifMatch))
            {
                return BadRequest($"'{HeaderNames.IfMatch}' request header must be specified.");
            }

            if (string.IsNullOrEmpty(author))
            {
                return BadRequest($"'{Headers.HeaderNames.AmsAuthor}' request header must be specified.");
            }

            if (templateDescriptor == null)
            {
                return BadRequest("Template descriptor must be set.");
            }

            try
            {
                var latestVersionId = await _templatesManagementService.ModifyTemplate(id, ifMatch.Trim('"'), author, templateDescriptor);
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
                return Unprocessable(GenerateTemplateErrorJson(ex));
            }
            catch (SessionLockAlreadyExistsException)
            {
                return Conflict();
            }
            catch (ConcurrencyException)
            {
                return PreconditionFailed();
            }
        }

        [SuppressMessage("StyleCop.CSharp.LayoutRules", "SA1502:ElementMustNotBeOnSingleLine", Justification = "Reviewed. Suppression is OK here.")]
        private static JToken GenerateTemplateErrorJson(TemplateValidationException ex) => new JArray { ex.SerializeToJson() };
    }
}