using System.IO;
using FileAction.Core;
using ImageMagick;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Windows.Media.Ocr;
using Windows.Storage;

namespace FileAction.App.Ocr;

internal sealed record PixelRegion(int X, int Y, int Width, int Height);

/// <summary>Suggests redaction rectangles using only the face and text engines built into Windows.</summary>
internal static class ImagePrivacyDetector
{
    public static async Task<IReadOnlyList<PixelRegion>> DetectAsync(string imagePath, bool faces, bool text)
    {
        if (!faces && !text)
            return [];
        if (faces && !FaceDetector.IsSupported)
            throw new ConversionException("Face detection is not supported by this Windows installation.");
        OcrEngine? ocr = null;
        if (text)
        {
            ocr = OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(engine => engine is not null)
                ?? throw new ConversionException("Windows has no text recognition installed. Add a language in Settings → Time & language → Language & region.");
        }

        var temporaryDirectory = OutputPaths.TempDirectory();
        try
        {
            var oriented = Path.Combine(temporaryDirectory, "oriented.png");
            // The preview applies AutoOrient too, so all returned rectangles use its exact pixel coordinates.
            await Task.Run(() =>
            {
                using var image = new MagickImage(imagePath, new MagickReadSettings { FrameIndex = 0, FrameCount = 1 });
                image.AutoOrient();
                image.Strip();
                image.Write(oriented, MagickFormat.Png);
            }).ConfigureAwait(false);
            var file = await StorageFile.GetFileFromPathAsync(oriented);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var regions = new List<PixelRegion>();

            if (faces)
            {
                if (!FaceDetector.IsBitmapPixelFormatSupported(BitmapPixelFormat.Gray8))
                    throw new ConversionException("The Windows face detector cannot read the required image format on this machine.");
                var transform = Transform(decoder, 2048);
                // WIC cannot decode every PNG directly to Gray8; decode BGRA first, then convert.
                using var decoded = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                using var bitmap = SoftwareBitmap.Convert(decoded, BitmapPixelFormat.Gray8);
                var detector = await FaceDetector.CreateAsync();
                foreach (var detected in await detector.DetectFacesAsync(bitmap))
                {
                    var box = detected.FaceBox;
                    AddRegion(regions, box.X, box.Y, box.Width, box.Height, transform, decoder);
                }
            }
            if (ocr is not null)
            {
                var transform = Transform(decoder, OcrEngine.MaxImageDimension);
                using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                var recognized = await ocr.RecognizeAsync(bitmap);
                foreach (var line in recognized.Lines)
                foreach (var word in line.Words)
                {
                    var box = word.BoundingRect;
                    AddRegion(regions, box.X, box.Y, box.Width, box.Height, transform, decoder);
                }
            }
            return regions.Distinct().ToArray();
        }
        catch (ConversionException) { throw; }
        catch (Exception ex)
        {
            throw new ConversionException("Windows could not analyze this image: " + ex.Message, ex);
        }
        finally
        {
            OutputPaths.TryDelete(temporaryDirectory);
        }
    }

    private static BitmapTransform Transform(BitmapDecoder decoder, uint maxDimension)
    {
        var scale = Math.Min(1.0, (double)maxDimension / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        return new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
    }

    private static void AddRegion(List<PixelRegion> regions, double x, double y, double width, double height,
        BitmapTransform transform, BitmapDecoder decoder)
    {
        var scaleX = (double)decoder.PixelWidth / transform.ScaledWidth;
        var scaleY = (double)decoder.PixelHeight / transform.ScaledHeight;
        var left = Math.Clamp((int)Math.Floor(x * scaleX), 0, (int)decoder.PixelWidth);
        var top = Math.Clamp((int)Math.Floor(y * scaleY), 0, (int)decoder.PixelHeight);
        var right = Math.Clamp((int)Math.Ceiling((x + width) * scaleX), left, (int)decoder.PixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling((y + height) * scaleY), top, (int)decoder.PixelHeight);
        if (right > left && bottom > top)
            regions.Add(new PixelRegion(left, top, right - left, bottom - top));
    }
}
