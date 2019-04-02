using System;

namespace NuClear.VStore.Objects
{
    public class ObjectUpgradeException : Exception
    {
        public ObjectUpgradeException(long objectId, string details)
            : base($"Object '{objectId}' cannot be upgraded. Details: {details}")
        {
            ObjectId = objectId;
        }

        public long ObjectId { get; }
    }
}