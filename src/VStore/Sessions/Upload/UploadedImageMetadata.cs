using NuClear.VStore.Descriptors;

namespace NuClear.VStore.Sessions.Upload
{
    public sealed class UploadedImageMetadata : IUploadedFileMetadata
    {
        public UploadedImageMetadata(string fileName, string contentType, long fileLength, ImageSize size)
        {
            FileName = fileName;
            ContentType = contentType;
            FileLength = fileLength;
            Size = size;
        }

        public FileType FileType => FileType.SizeSpecificBitmapImage;

        public string FileName { get; }

        public string ContentType { get; }

        public long FileLength { get; }

        public ImageSize Size { get; }
    }
}