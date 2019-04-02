namespace NuClear.VStore.Sessions.Upload
{
    public sealed class GenericUploadedFileMetadata : IUploadedFileMetadata
    {
        public GenericUploadedFileMetadata(string fileName, string contentType, long fileLength)
        {
            FileName = fileName;
            ContentType = contentType;
            FileLength = fileLength;
        }

        public FileType FileType => FileType.NotSet;

        public string FileName { get; }

        public string ContentType { get; }

        public long FileLength { get; }
    }
}