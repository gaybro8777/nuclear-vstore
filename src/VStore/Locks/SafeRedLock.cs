using Microsoft.Extensions.Logging;

using RedLockNet;

namespace NuClear.VStore.Locks
{
    public sealed class SafeRedLock : IRedLock
    {
        private readonly ILogger _logger;
        private readonly IRedLock _innerRedLock;

        public SafeRedLock(ILogger logger, IRedLock innerRedLock)
        {
            _logger = logger;
            _innerRedLock = innerRedLock;
        }

        public string Resource => _innerRedLock.Resource;
        public string LockId => _innerRedLock.LockId;
        public bool IsAcquired => _innerRedLock.IsAcquired;
        public int ExtendCount => _innerRedLock.ExtendCount;

        public RedLockStatus Status => _innerRedLock.Status;

        public RedLockInstanceSummary InstanceSummary => _innerRedLock.InstanceSummary;

        public void Dispose()
        {
            try
            {
                _innerRedLock.Dispose();
            }
            catch
            {
                _logger.LogWarning("Resource {resource} hadn't been unlocked. One must wait for an expiration period set on lock creation.", Resource);
            }
        }
    }
}