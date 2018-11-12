using System;

namespace NuClear.VStore.Sessions.Fetch
{
    public class InvalidFetchUrlException : Exception
    {
        public InvalidFetchUrlException(string message) : base(message)
        {
        }
    }
}