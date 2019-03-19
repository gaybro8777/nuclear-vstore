using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using NuClear.VStore.Descriptors.Objects;
using NuClear.VStore.Options;
using NuClear.VStore.S3;
using NuClear.VStore.Sessions;
using NuClear.VStore.Sessions.ContentValidation.Errors;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;
using SixLabors.Primitives;
using SixLabors.Shapes;

namespace NuClear.VStore.ImageRendering
{
    public sealed class ImagePreviewService
    {
        private static readonly Dictionary<string, IImageEncoder> Encoders =
            new Dictionary<string, IImageEncoder>
                {
                    { JpegFormat.Instance.DefaultMimeType, new JpegEncoder { Quality = 100 } },
                    { PngFormat.Instance.DefaultMimeType, new PngEncoder { CompressionLevel = 1 } },
                    { GifFormat.Instance.DefaultMimeType, new GifEncoder() }
                };

        private readonly string _bucketName;
        private readonly TimeSpan _requestTimeout;

        private readonly IS3Client _s3Client;
        private readonly IRequestLimiter _requestLimiter;

        public ImagePreviewService(
            CephOptions cephOptions,
            ThrottlingOptions throttlingOptions,
            IS3Client s3Client,
            IRequestLimiter requestLimiter)
        {
            _bucketName = cephOptions.FilesBucketName;
            _requestTimeout = throttlingOptions.RequestTimeout;
            _s3Client = s3Client;
            _requestLimiter = requestLimiter;
        }

        public async Task<(Stream imageStream, string contentType)> GetCroppedPreview(
            IImageElementValue imageElementValue,
            int templateCode,
            int width,
            int height)
        {
            return await GetPngEncodedPreview(imageElementValue, templateCode, width, height, null);
        }

        [Obsolete]
        public async Task<(Stream imageStream, string contentType)> GetCroppedAndRoundedPreview(
            IImageElementValue imageElementValue,
            int templateCode,
            int width,
            int height)
        {
            return await GetPngEncodedPreview(
                       imageElementValue,
                       templateCode,
                       width,
                       height,
                       target => ApplyRoundedCorners(target, target.Height * 0.5f));
        }

        public async Task<(Stream imageStream, string contentType)> GetScaledPreview(
            IImageElementValue imageElementValue,
            int templateCode,
            int width,
            int height)
        {
            var cts = new CancellationTokenSource(_requestTimeout);
            var rawStream = await GetRawStream(imageElementValue, cts.Token);

            await EnsureRequestCanBeProcessed(rawStream, cts.Token);

            using (var source = Decode(templateCode, rawStream, out var imageFormat))
            {
                var anchorPositionMode = EvaluateAnchorPositionMode(imageElementValue);
                Resize(source, new Size(width, height), anchorPositionMode);

                var targetStream = Encode(source, imageFormat);
                return (targetStream, imageFormat.DefaultMimeType);
            }
        }

        private static Image<Rgba32> Decode(int templateCode, Stream sourceStream, out IImageFormat imageFormat)
        {
            using (sourceStream)
            {
                Image<Rgba32> image;
                try
                {
                    sourceStream.Position = 0;
                    image = Image.Load(sourceStream, out imageFormat);
                }
                catch
                {
                    throw new InvalidBinaryException(templateCode, new InvalidImageError());
                }

                return image;
            }
        }

        private static Image<Rgba32> Crop(Image<Rgba32> image, IImageElementValue elementValue)
        {
            if (!(elementValue is ICompositeBitmapImageElementValue compositeBitmapImage))
            {
                return image;
            }

            var imageRectangle = image.Bounds();
            var cropAreaRectangle = new Rectangle(
                compositeBitmapImage.CropArea.Left,
                compositeBitmapImage.CropArea.Top,
                compositeBitmapImage.CropArea.Width,
                compositeBitmapImage.CropArea.Height);

            if (!imageRectangle.IntersectsWith(cropAreaRectangle))
            {
                return image;
            }

            if (imageRectangle.Contains(cropAreaRectangle))
            {
                image.Mutate(ctx => ctx.Crop(cropAreaRectangle));
                return image;
            }

            Rgba32 backgroundColor;

            var leftTopPixel = image[imageRectangle.Left, imageRectangle.Top];
            var rightTopPixel = image[imageRectangle.Right - 1, imageRectangle.Top];
            var rightBottomPixel = image[imageRectangle.Right - 1, imageRectangle.Bottom - 1];
            var leftBottomPixel = image[imageRectangle.Left, imageRectangle.Bottom - 1];

            if (ArePixelColorsClose(leftTopPixel, rightBottomPixel) &&
                ArePixelColorsClose(rightTopPixel, rightBottomPixel) &&
                ArePixelColorsClose(rightBottomPixel, leftBottomPixel) &&
                ArePixelColorsClose(leftBottomPixel, leftTopPixel))
            {
                backgroundColor = leftTopPixel;
            }
            else
            {
                var redTop = GetMeanColorValue(image.Width, (intervalCount, index) => GetImagePixelColorOnTop(image, intervalCount, index).R);
                var greenTop = GetMeanColorValue(image.Width, (intervalCount, index) => GetImagePixelColorOnTop(image, intervalCount, index).G);
                var blueTop = GetMeanColorValue(image.Width, (intervalCount, index) => GetImagePixelColorOnTop(image, intervalCount, index).B);

                var redBottom = GetMeanColorValue(image.Width, (intervalCount, index) => GetImagePixelColorOnBottom(image, intervalCount, index).R);
                var greenBottom = GetMeanColorValue(image.Width, (intervalCount, index) => GetImagePixelColorOnBottom(image, intervalCount, index).G);
                var blueBottom = GetMeanColorValue(image.Width, (intervalCount, index) => GetImagePixelColorOnBottom(image, intervalCount, index).B);

                var redLeft = GetMeanColorValue(image.Height, (intervalCount, index) => GetImagePixelColorOnLeft(image, intervalCount, index).R);
                var greenLeft = GetMeanColorValue(image.Height, (intervalCount, index) => GetImagePixelColorOnLeft(image, intervalCount, index).G);
                var blueLeft = GetMeanColorValue(image.Height, (intervalCount, index) => GetImagePixelColorOnLeft(image, intervalCount, index).B);

                var redRight = GetMeanColorValue(image.Height, (intervalCount, index) => GetImagePixelColorOnRight(image, intervalCount, index).R);
                var greenRight = GetMeanColorValue(image.Height, (intervalCount, index) => GetImagePixelColorOnRight(image, intervalCount, index).G);
                var blueRight = GetMeanColorValue(image.Height, (intervalCount, index) => GetImagePixelColorOnRight(image, intervalCount, index).B);

                backgroundColor = new Rgba32(
                    (byte)((redTop + redBottom + redLeft + redRight) / 4),
                    (byte)((greenTop + greenBottom + greenLeft + greenRight) / 4),
                    (byte)((blueTop + blueBottom + blueLeft + blueRight) / 4));
            }

            var unionRectangle = Rectangle.Union(imageRectangle, cropAreaRectangle);
            cropAreaRectangle.Offset(-unionRectangle.X, -unionRectangle.Y);

            var extentImage = new Image<Rgba32>(unionRectangle.Width, unionRectangle.Height);
            extentImage.Mutate(x => x.BackgroundColor(backgroundColor)
                                     .DrawImage(image, new Point(-unionRectangle.X, -unionRectangle.Y), GraphicsOptions.Default)
                                     .Crop(cropAreaRectangle));

            return extentImage;
        }

        private static void Resize(Image<Rgba32> image, Size size)
        {
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = size,
                    Sampler = new Lanczos2Resampler(),
                    Mode = ResizeMode.Stretch
                }));
        }

        private static void Resize(Image<Rgba32> image, Size size, AnchorPositionMode anchorPositionModel)
        {
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = size,
                    Sampler = new Lanczos2Resampler(),
                    Mode = ResizeMode.Crop,
                    Position = anchorPositionModel
                }));
        }

        private static AnchorPositionMode EvaluateAnchorPositionMode(IImageElementValue imageElementValue)
        {
            if (!(imageElementValue is IScalableBitmapImageElementValue scalableBitmapImageElementValue))
            {
                return AnchorPositionMode.Center;
            }

            switch (scalableBitmapImageElementValue.Anchor)
            {
                case Anchor.TopLeft:
                    return AnchorPositionMode.TopLeft;
                case Anchor.Top:
                    return AnchorPositionMode.Top;
                case Anchor.TopRight:
                    return AnchorPositionMode.TopRight;
                case Anchor.Left:
                    return AnchorPositionMode.Left;
                case Anchor.Middle:
                    return AnchorPositionMode.Center;
                case Anchor.Right:
                    return AnchorPositionMode.Right;
                case Anchor.BottomLeft:
                    return AnchorPositionMode.BottomLeft;
                case Anchor.Bottom:
                    return AnchorPositionMode.Bottom;
                case Anchor.BottomRight:
                    return AnchorPositionMode.BottomRight;
                default:
                    return AnchorPositionMode.Center;
            }
        }

        private static void ApplyRoundedCorners(Image<Rgba32> image, float cornerRadius)
        {
            var corners = GetClippedRect(image.Width, image.Height, cornerRadius);
            image.Mutate(ctx => ctx.Fill(
                             new GraphicsOptions { AlphaCompositionMode = PixelAlphaCompositionMode.Src, Antialias = false },
                             Rgba32.Transparent,
                             corners));
        }

        private static IPath GetClippedRect(int imageWidth, int imageHeight, float cornerRadius)
        {
            var rect = new RectangularPolygon(-0.5f, -0.5f, imageWidth + 0.5f, imageHeight + 0.5f);
            return rect.Clip(new EllipsePolygon(imageWidth * 0.5f, imageHeight * 0.5f, cornerRadius));
        }

        private static bool ArePixelColorsClose(Rgba32 pixel1, Rgba32 pixel2)
        {
            const double Epsilon = 0.03 * byte.MaxValue;
            return Math.Abs(pixel1.R - pixel2.R) < Epsilon &&
                   Math.Abs(pixel1.G - pixel2.G) < Epsilon &&
                   Math.Abs(pixel1.B - pixel2.B) < Epsilon;
        }

        // Based on Simpson's rule
        private static byte GetMeanColorValue(int dimension, Func<int, int, byte> getPixelColor)
        {
            var intervalCount = dimension / 4;

            var value1 = 0;
            for (var index = 1; index < 2 * intervalCount - 1; index += 2)
            {
                value1 += getPixelColor(intervalCount, index);
            }

            var value2 = 0;
            for (var index = 2; index < 2 * intervalCount - 2; index += 2)
            {
                value2 += getPixelColor(intervalCount, index);
            }

            var sum = getPixelColor(intervalCount, 0) + 4 * value1 + 2 * value2 + getPixelColor(intervalCount, 2 * intervalCount);
            return (byte)(sum / (float)intervalCount / 6f);
        }

        private static Rgb24 GetImagePixelColorOnTop(Image<Rgba32> image, int intervalCount, int index)
            => image[(image.Width - 1) * index / (2 * intervalCount), 0].Rgb;

        private static Rgb24 GetImagePixelColorOnBottom(Image<Rgba32> image, int intervalCount, int index)
            => image[(image.Width - 1) * index / (2 * intervalCount), image.Height - 1].Rgb;

        private static Rgb24 GetImagePixelColorOnLeft(Image<Rgba32> image, int intervalCount, int index)
            => image[0, (image.Height - 1) * index / (2 * intervalCount)].Rgb;

        private static Rgb24 GetImagePixelColorOnRight(Image<Rgba32> image, int intervalCount, int index)
            => image[image.Width - 1, (image.Height - 1) * index / (2 * intervalCount)].Rgb;

        private async Task<(Stream imageStream, string contentType)> GetPngEncodedPreview(
            IImageElementValue imageElementValue,
            int templateCode,
            int width,
            int height,
            Action<Image<Rgba32>> mutateImage)
        {
            var cts = new CancellationTokenSource(_requestTimeout);

            var rawStream = await GetRawStream(imageElementValue, cts.Token);

            await EnsureRequestCanBeProcessed(rawStream, cts.Token);

            using (var source = Decode(templateCode, rawStream, out _))
            {
                using (var target = Crop(source, imageElementValue))
                {
                    Resize(target, new Size(width, height));
                    mutateImage?.Invoke(target);

                    var pngFormat = PngFormat.Instance;
                    var targetStream = Encode(target, pngFormat);
                    return (targetStream, pngFormat.DefaultMimeType);
                }
            }
        }

        private MemoryStream Encode(Image<Rgba32> image, IImageFormat format)
        {
            var imageStream = new MemoryStream();
            image.Save(imageStream, Encoders[format.DefaultMimeType]);
            imageStream.Position = 0;

            return imageStream;
        }

        private async Task<MemoryStream> GetRawStream(IObjectElementRawValue imageElementValue, CancellationToken token)
        {
            var response = await _s3Client.GetObjectAsync(_bucketName, imageElementValue.Raw, token);
            using (response.ResponseStream)
            {
                var memoryStream = new MemoryStream();
                response.ResponseStream.CopyTo(memoryStream);
                memoryStream.Position = 0;

                return memoryStream;
            }
        }

        private async Task EnsureRequestCanBeProcessed(Stream rawStream, CancellationToken cancellationToken)
        {
            rawStream.Position = 0;
            var imageInfo = Image.Identify(rawStream);
            var requiredMemoryInBytes = imageInfo.PixelType.BitsPerPixel / 8 * imageInfo.Width * imageInfo.Width;

            await _requestLimiter.HandleRequestAsync(requiredMemoryInBytes, cancellationToken);
        }
    }
}