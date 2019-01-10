using System;
using System.Net;

namespace NuClear.VStore.Sessions.Fetch
{
    public class FetchRequestException : Exception
    {
        public HttpStatusCode StatusCode { get; }

        public FetchRequestException(HttpStatusCode statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }
    }
}