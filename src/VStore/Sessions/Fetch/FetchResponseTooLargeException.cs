using System;

namespace NuClear.VStore.Sessions.Fetch
{
    public class FetchResponseTooLargeException : Exception
    {
        public long ContentLength { get; }

        public FetchResponseTooLargeException(long contentLength) : base($"Fetch response is too large: {contentLength}")
        {
            ContentLength = contentLength;
        }
    }
}