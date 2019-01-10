using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

using Newtonsoft.Json.Linq;

using NuClear.VStore.Http.Core.ActionResults;
using ConflictResult = NuClear.VStore.Http.Core.ActionResults.ConflictResult;
using NoContentResult = NuClear.VStore.Http.Core.ActionResults.NoContentResult;

namespace NuClear.VStore.Http.Core.Controllers
{
    [ApiController]
    public abstract class VStoreController : ControllerBase
    {
        [NonAction]
        public NoContentResult NoContent(string location) => new NoContentResult(location);

        [NonAction]
        public bool TryGetModelErrors(out IReadOnlyCollection<string> errors)
        {
            if (ModelState.IsValid)
            {
                errors = Array.Empty<string>();
            }
            else
            {
                errors = ModelState.Values.Where(x => x.ValidationState == ModelValidationState.Invalid)
                                   .SelectMany(x => x.Errors)
                                   .Select(error => string.IsNullOrEmpty(error.ErrorMessage) ? error.Exception?.Message : error.ErrorMessage)
                                   .ToList();
            }

            return !ModelState.IsValid;
        }

        [NonAction]
        public virtual JsonResult Json(object data) => new JsonResult(data);

        [NonAction]
        public ConflictResult Conflict(string message) => new ConflictResult(message) { ContentType = ContentType.PlainText };

        [NonAction]
        public LockedResult Locked(string message) => new LockedResult(message) { ContentType = ContentType.PlainText };

        [NonAction]
        public PreconditionFailedResult PreconditionFailed() => new PreconditionFailedResult();

        [NonAction]
        public UnprocessableResult Unprocessable(JToken value)
            => new UnprocessableResult(value) { ContentTypes = new MediaTypeCollection { ContentType.Json } };

        [NonAction]
        public UnprocessableResult Unprocessable(string value)
            => new UnprocessableResult(value) { ContentTypes = new MediaTypeCollection { ContentType.PlainText } };

        [NonAction]
        public GoneResult Gone(DateTime expiresAt) => new GoneResult(expiresAt);

        [NonAction]
        public TooManyRequestsResult TooManyRequests(TimeSpan retryAfter) => new TooManyRequestsResult(retryAfter);

        [NonAction]
        public NotModifiedResult NotModified() => new NotModifiedResult();

        [NonAction]
        public BadRequestContentResult BadRequest(string message)
            => new BadRequestContentResult(message) { ContentType = ContentType.PlainText };

        [NonAction]
        public RequestTooLargeContentResult RequestTooLarge(string message)
            => new RequestTooLargeContentResult(message) { ContentType = ContentType.PlainText };

        [NonAction]
        public FailedDependencyContentResult FailedDependency(string message)
            => new FailedDependencyContentResult(message) { ContentType = ContentType.PlainText };

        [NonAction]
        public ServiceUnavailableResult ServiceUnavailable(string message)
            => new ServiceUnavailableResult(message) { ContentType = ContentType.PlainText };
    }
}