using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using NuClear.VStore.Descriptors;
using NuClear.VStore.Descriptors.Templates;
using NuClear.VStore.Sessions.ContentValidation.Errors;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace NuClear.VStore.Sessions.ContentValidation
{
    public static class BitmapImageValidator
    {
        private static readonly IReadOnlyDictionary<FileFormat, string> ImageFormatToMimeTypeMap =
            new Dictionary<FileFormat, string>
                {
                    { FileFormat.Bmp, BmpFormat.Instance.DefaultMimeType },
                    { FileFormat.Gif, GifFormat.Instance.DefaultMimeType },
                    { FileFormat.Jpeg, JpegFormat.Instance.DefaultMimeType },
                    { FileFormat.Jpg, JpegFormat.Instance.DefaultMimeType },
                    { FileFormat.Png, PngFormat.Instance.DefaultMimeType }
                };

        public static void ValidateBitmapImageHeader(
            int templateCode,
            BitmapImageElementConstraints constraints,
            FileFormat fileFormat,
            Stream inputStream)
        {
            var imageInfo = ValidateBitmapImageFormat(templateCode, constraints, fileFormat, inputStream);

            if (constraints.SupportedImageSizes.All(x => imageInfo.Width != x.Width || imageInfo.Height != x.Height))
            {
                throw new InvalidBinaryException(templateCode, new ImageUnsupportedSizeError(new ImageSize { Height = imageInfo.Height, Width = imageInfo.Width }));
            }
        }

        public static void ValidateSizeRangedBitmapImageHeader(
            int templateCode,
            ISizeRangedImageElementConstraints constraints,
            FileFormat fileFormat,
            Stream inputStream)
        {
            var imageInfo = ValidateBitmapImageFormat(templateCode, constraints, fileFormat, inputStream);

            var imageSize = new ImageSize { Width = imageInfo.Width, Height = imageInfo.Height };
            if (!constraints.ImageSizeRange.Includes(imageSize))
            {
                throw new InvalidBinaryException(templateCode, new ImageSizeOutOfRangeError(imageSize));
            }
        }

        public static void ValidateSizeSpecificBitmapImageHeader(
            int templateCode,
            CompositeBitmapImageElementConstraints constraints,
            FileFormat fileFormat,
            Stream inputStream,
            ImageSize targetImageSize)
        {
            var imageInfo = ValidateBitmapImageFormat(templateCode, constraints, fileFormat, inputStream);

            var imageSize = new ImageSize { Width = imageInfo.Width, Height = imageInfo.Height };
            if (imageSize != targetImageSize)
            {
                throw new InvalidBinaryException(templateCode, new SizeSpecificImageTargetSizeNotEqualToActualSizeError(imageSize));
            }

            if (constraints.SizeSpecificImageIsSquare && imageSize.Width != imageSize.Height)
            {
                throw new InvalidBinaryException(templateCode, new SizeSpecificImageIsNotSquareError());
            }
        }

        public static void ValidateBitmapImage(int templateCode, BitmapImageElementConstraints constraints, Stream inputStream)
        {
            if (!constraints.IsAlphaChannelRequired)
            {
                return;
            }

            Image<Rgba32> decodedImage;
            try
            {
                inputStream.Position = 0;
                decodedImage = Image.Load(inputStream);
            }
            catch (Exception)
            {
                throw new InvalidBinaryException(templateCode, new InvalidImageError());
            }

            if (!IsImageContainsAlphaChannel(decodedImage))
            {
                throw new InvalidBinaryException(templateCode, new ImageMissingAlphaChannelError());
            }
        }

        private static IImageInfo ValidateBitmapImageFormat(int templateCode, IBinaryElementConstraints constraints, FileFormat fileFormat, Stream inputStream)
        {
            var imageFormats =
                constraints.SupportedFileFormats
                           .Aggregate(
                               new List<string>(),
                               (result, next) =>
                                   {
                                       if (ImageFormatToMimeTypeMap.TryGetValue(next, out var imageFormat))
                                       {
                                           result.Add(imageFormat);
                                       }

                                       return result;
                                   });

            inputStream.Position = 0;
            var format = Image.DetectFormat(inputStream);
            if (format == null)
            {
                throw new InvalidBinaryException(templateCode, new InvalidImageError());
            }

            var extension = fileFormat.ToString().ToLowerInvariant();
            if (!format.FileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                // Image format is not consistent with the filename extension
                throw new InvalidBinaryException(templateCode, new BinaryExtensionMismatchContentError(extension, format.Name.ToLowerInvariant()));
            }

            if (!imageFormats.Contains(format.DefaultMimeType, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidBinaryException(templateCode, new BinaryInvalidFormatError(format.Name.ToLowerInvariant()));
            }

            IImageInfo imageInfo;
            try
            {
                inputStream.Position = 0;
                imageInfo = Image.Identify(inputStream);
            }
            catch (Exception)
            {
                throw new InvalidBinaryException(templateCode, new InvalidImageError());
            }

            return imageInfo;
        }

        private static bool IsImageContainsAlphaChannel(Image<Rgba32> image)
        {
            for (var x = 0; x < image.Width; ++x)
            {
                for (var y = 0; y < image.Height; ++y)
                {
                    if (image[x, y].A != byte.MaxValue)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
