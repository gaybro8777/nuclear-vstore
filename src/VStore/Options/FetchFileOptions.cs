namespace NuClear.VStore.Options
{
    public sealed class FetchFileOptions
    {
        public long MaxBinarySize { get; set; }
        public int MaxRetryCount { get; set; }
        public int MaxTimeoutMs { get; set; }
    }
}