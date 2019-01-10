using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace NuClear.VStore.Http.Core.ActionResults
{
    public class FailedDependencyContentResult : ContentResult
    {
        public FailedDependencyContentResult(string message)
        {
            StatusCode = StatusCodes.Status424FailedDependency;
            Content = message;
        }
    }
}