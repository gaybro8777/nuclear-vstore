using System;

namespace NuClear.VStore.Sessions.Fetch
{
    public class FetchResponseContentTypeInvalidException : Exception
    {
        public FetchResponseContentTypeInvalidException(string message) : base(message)
        {
        }
    }
}