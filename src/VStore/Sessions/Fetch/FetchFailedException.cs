using System;

namespace NuClear.VStore.Sessions.Fetch
{
    public class FetchFailedException : Exception
    {
        public FetchFailedException(string message) : base(message)
        {
        }
    }
}