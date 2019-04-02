namespace NuClear.VStore.Descriptors.Sessions
{
    public sealed class BinaryMetadata
    {
        public BinaryMetadata(string filename, long fileSize)
        {
            Filename = filename;
            FileSize = fileSize;
        }

        public string Filename { get; }
        public long FileSize { get; }
    }
}