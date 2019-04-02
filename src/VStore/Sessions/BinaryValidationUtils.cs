using System;
using System.IO;
using System.Linq;

using NuClear.VStore.Descriptors;
using NuClear.VStore.Descriptors.Templates;
using NuClear.VStore.Sessions.ContentValidation;
using NuClear.VStore.Sessions.ContentValidation.Errors;
using NuClear.VStore.Sessions.Upload;

namespace NuClear.VStore.Sessions
{
    public static class BinaryValidationUtils
    {
        public static void EnsureFileMetadataIsValid(IElementDescriptor elementDescriptor, Language language, IUploadedFileMetadata uploadedFileMetadata)
        {
            var constraints = (IBinaryElementConstraints)elementDescriptor.Constraints.For(language);
            if (constraints.MaxFilenameLength < uploadedFileMetadata.FileName.Length)
            {
                throw new InvalidBinaryException(elementDescriptor.TemplateCode, new FilenameTooLongError(uploadedFileMetadata.FileName.Length));
            }

            if (constraints.MaxSize < uploadedFileMetadata.FileLength)
            {
                throw new InvalidBinaryException(elementDescriptor.TemplateCode, new BinaryTooLargeError(uploadedFileMetadata.FileLength));
            }

            if (constraints is CompositeBitmapImageElementConstraints compositeBitmapImageElementConstraints &&
                uploadedFileMetadata.FileType == FileType.SizeSpecificBitmapImage &&
                compositeBitmapImageElementConstraints.SizeSpecificImageMaxSize < uploadedFileMetadata.FileLength)
            {
                throw new InvalidBinaryException(elementDescriptor.TemplateCode, new SizeSpecificImageTooLargeError(uploadedFileMetadata.FileLength));
            }

            var extension = GetDotLessExtension(uploadedFileMetadata.FileName);
            if (!ValidateFileExtension(extension, constraints))
            {
                throw new InvalidBinaryException(elementDescriptor.TemplateCode, new BinaryInvalidFormatError(extension));
            }
        }

        public static void EnsureFileHeaderIsValid(
            int templateCode,
            ElementDescriptorType elementDescriptorType,
            IElementConstraints elementConstraints,
            Stream inputStream,
            IUploadedFileMetadata uploadedFileMetadata)
        {
            var fileFormat = DetectFileFormat(uploadedFileMetadata.FileName);
            switch (elementDescriptorType)
            {
                case ElementDescriptorType.BitmapImage:
                    BitmapImageValidator.ValidateBitmapImageHeader(
                        templateCode,
                        (BitmapImageElementConstraints)elementConstraints,
                        fileFormat,
                        inputStream);
                    break;
                case ElementDescriptorType.ScalableBitmapImage:
                    BitmapImageValidator.ValidateSizeRangedBitmapImageHeader(
                        templateCode,
                        (ScalableBitmapImageElementConstraints)elementConstraints,
                        fileFormat,
                        inputStream);
                    break;
                case ElementDescriptorType.CompositeBitmapImage:
                    if (uploadedFileMetadata.FileType == FileType.SizeSpecificBitmapImage)
                    {
                        var imageMetadata = (UploadedImageMetadata)uploadedFileMetadata;
                        BitmapImageValidator.ValidateSizeSpecificBitmapImageHeader(
                            templateCode,
                            (CompositeBitmapImageElementConstraints)elementConstraints,
                            fileFormat,
                            inputStream,
                            imageMetadata.Size);
                    }
                    else
                    {
                        BitmapImageValidator.ValidateSizeRangedBitmapImageHeader(
                            templateCode,
                            (CompositeBitmapImageElementConstraints)elementConstraints,
                            fileFormat,
                            inputStream);
                    }

                    break;
                case ElementDescriptorType.VectorImage:
                    VectorImageValidator.ValidateVectorImageHeader(templateCode, fileFormat, inputStream);
                    break;
                case ElementDescriptorType.Article:
                    break;
                case ElementDescriptorType.PlainText:
                case ElementDescriptorType.FormattedText:
                case ElementDescriptorType.FasComment:
                case ElementDescriptorType.Link:
                case ElementDescriptorType.Phone:
                case ElementDescriptorType.VideoLink:
                case ElementDescriptorType.Color:
                    throw new NotSupportedException($"Specified element descriptor type '{elementDescriptorType}' is non-binary");
                default:
                    throw new ArgumentOutOfRangeException(nameof(elementDescriptorType), elementDescriptorType, "Unsupported element descriptor type");
            }
        }

        public static void EnsureFileContentIsValid(
            int templateCode,
            ElementDescriptorType elementDescriptorType,
            IElementConstraints elementConstraints,
            Stream inputStream,
            string fileName)
        {
            switch (elementDescriptorType)
            {
                case ElementDescriptorType.BitmapImage:
                    BitmapImageValidator.ValidateBitmapImage(templateCode, (BitmapImageElementConstraints)elementConstraints, inputStream);
                    break;
                case ElementDescriptorType.VectorImage:
                    var fileFormat = DetectFileFormat(fileName);
                    VectorImageValidator.ValidateVectorImage(templateCode, fileFormat, (VectorImageElementConstraints)elementConstraints, inputStream);
                    break;
                case ElementDescriptorType.Article:
                    ArticleValidator.ValidateArticle(templateCode, inputStream);
                    break;
                case ElementDescriptorType.CompositeBitmapImage:
                    break;
                case ElementDescriptorType.ScalableBitmapImage:
                    break;
                case ElementDescriptorType.PlainText:
                case ElementDescriptorType.FormattedText:
                case ElementDescriptorType.FasComment:
                case ElementDescriptorType.Link:
                case ElementDescriptorType.Phone:
                case ElementDescriptorType.VideoLink:
                case ElementDescriptorType.Color:
                    throw new NotSupportedException($"Specified element descriptor type '{elementDescriptorType}' is non-binary");
                default:
                    throw new ArgumentOutOfRangeException(nameof(elementDescriptorType), elementDescriptorType, "Unsupported element descriptor type");
            }
        }

        private static FileFormat DetectFileFormat(string fileName)
        {
            var extension = GetDotLessExtension(fileName);
            if (Enum.TryParse(extension, true, out FileFormat format))
            {
                return format;
            }

            throw new ArgumentException($"Filename '{fileName}' does not have appropriate extension", nameof(fileName));
        }

        private static bool ValidateFileExtension(string extension, IBinaryElementConstraints constraints) =>
            Enum.TryParse(extension, true, out FileFormat format)
            && Enum.IsDefined(typeof(FileFormat), format)
            && format.ToString().Equals(extension, StringComparison.OrdinalIgnoreCase)
            && constraints.SupportedFileFormats.Any(f => f == format);

        private static string GetDotLessExtension(string path)
        {
            var dottedExtension = Path.GetExtension(path);
            return string.IsNullOrEmpty(dottedExtension)
                       ? dottedExtension
                       : dottedExtension.Trim('.')
                                        .ToLowerInvariant();
        }
    }
}