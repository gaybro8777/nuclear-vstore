using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace NuClear.VStore.Http.Core.ActionResults
{
    public class RequestTooLargeContentResult : ContentResult
    {
        public RequestTooLargeContentResult(string message)
        {
            StatusCode = StatusCodes.Status413PayloadTooLarge;
            Content = message;
        }
    }
}
