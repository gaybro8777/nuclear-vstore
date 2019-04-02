namespace NuClear.VStore.Sessions.Upload
{
    public interface IUploadedFileMetadata
    {
        FileType FileType { get; }

        string FileName { get; }

        string ContentType { get; }

        long FileLength { get; }
    }
}