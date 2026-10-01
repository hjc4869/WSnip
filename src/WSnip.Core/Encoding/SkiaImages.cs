using SkiaSharp;
using WSnip.Core.Imaging;

using WSnip.Core.Strings;

namespace WSnip.Core.Encoding;

internal static class SkiaImages
{
    public static SKColorSpace ColorSpace(ColorPrimaries primaries) => primaries switch
    {
        ColorPrimaries.DisplayP3 => SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3),
        ColorPrimaries.Bt2020 => SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.Rec2020),
        _ => SKColorSpace.CreateSrgb(),
    };

    /// <summary>Runs an action with a pixmap over the SDR base of a rendition.</summary>
    public static unsafe T WithSdrPixmap<T>(Rendition rendition, Func<SKPixmap, T> action)
    {
        // A fixed pointer into native memory does not root its managed owner. Keep an explicit
        // lease through the entire encoder callback, including calls made with temporary inputs.
        using PixelBuffer<byte> retained = rendition.Sdr.Share();
        using SKColorSpace colorSpace = ColorSpace(rendition.Primaries);
        var info = new SKImageInfo(rendition.Width, rendition.Height, SKColorType.Rgba8888,
            rendition.HasAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque, colorSpace);
        fixed (byte* pixels = retained)
        {
            using var pixmap = new SKPixmap(info, (IntPtr)pixels, rendition.Width * 4);
            return action(pixmap);
        }
    }

    /// <summary>Encodes untagged 8-bit samples, one or three channels, as a baseline 4:4:4 JPEG.</summary>
    public static unsafe byte[] EncodeSamplesJpeg(PixelBuffer<byte> samples, int width, int height, int channels, int quality)
    {
        using PixelBuffer<byte> expanded = channels == 3
            ? new PixelBuffer<byte>(checked(width * height * 4))
            : samples.Share();
        PixelBuffer<byte> pixels = expanded;
        SKColorType type = SKColorType.Gray8;
        int rowBytes = width;
        if (channels == 3)
        {
            for (int i = 0, j = 0; i < samples.Length; i += 3, j += 4)
            {
                pixels[j] = samples[i];
                pixels[j + 1] = samples[i + 1];
                pixels[j + 2] = samples[i + 2];
                pixels[j + 3] = 255;
            }

            type = SKColorType.Rgba8888;
            rowBytes = width * 4;
        }

        var info = new SKImageInfo(width, height, type, SKAlphaType.Opaque);
        fixed (byte* data = pixels)
        {
            using var pixmap = new SKPixmap(info, (IntPtr)data, rowBytes);
            using SKData? encoded = pixmap.Encode(new SKJpegEncoderOptions(
                quality, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.Ignore));
            return encoded?.ToArray() ?? throw new InvalidOperationException(AppStrings.GainMapEncodingFailed);
        }
    }
}
