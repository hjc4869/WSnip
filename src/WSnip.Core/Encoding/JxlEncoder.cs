using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using WSnip.Core.Imaging;

namespace WSnip.Core.Encoding;

/// <summary>Writes JPEG XL through FFmpeg's libjxl wrapper: HDR as 16-bit BT.2100 PQ, SDR as 8-bit.</summary>
public static class JxlEncoder
{
    private const string EncoderName = "libjxl";

    public static bool IsAvailable() => FfmpegStillEncoder.IsAvailable(EncoderName);

    public static void WriteHdr(Stream output, PqImage image, int quality, int effort)
    {
        int channels = image.HasAlpha ? 4 : 3;
        using var packed = new PixelBuffer<byte>(checked(image.Width * image.Height * channels * 2));
        Span<ushort> samples = MemoryMarshal.Cast<byte, ushort>(packed.Span);
        if (channels == 4)
        {
            image.Pixels.Span.CopyTo(samples);
        }
        else
        {
            for (int p = 0; p < image.Width * image.Height; p++)
            {
                samples[p * 3] = image.Pixels[p * 4];
                samples[p * 3 + 1] = image.Pixels[p * 4 + 1];
                samples[p * 3 + 2] = image.Pixels[p * 4 + 2];
            }
        }

        using var picture = new PicturePlanes
        {
            Width = image.Width,
            Height = image.Height,
            Format = image.HasAlpha ? AVPixelFormat.AV_PIX_FMT_RGBA64LE : AVPixelFormat.AV_PIX_FMT_RGB48LE,
            Planes = [packed.Share()],
            Strides = [image.Width * channels * 2],
            Primaries = (AVColorPrimaries)ColorMath.CicpPrimaries(image.Primaries),
            Transfer = AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084,
            Matrix = AVColorSpace.AVCOL_SPC_RGB,
        };
        packed.Dispose();
        output.Write(FfmpegStillEncoder.Encode(EncoderName, picture, Options(quality, effort)));
    }

    public static void WriteSdr(Stream output, Rendition rendition, int quality, int effort)
    {
        using PixelBuffer<byte> samples = rendition.HasAlpha
            ? rendition.Sdr.Share()
            : new PixelBuffer<byte>(checked(rendition.Width * rendition.Height * 3));
        if (!rendition.HasAlpha)
        {
            for (int p = 0; p < rendition.Width * rendition.Height; p++)
            {
                samples[p * 3] = rendition.Sdr[p * 4];
                samples[p * 3 + 1] = rendition.Sdr[p * 4 + 1];
                samples[p * 3 + 2] = rendition.Sdr[p * 4 + 2];
            }
        }

        using var picture = new PicturePlanes
        {
            Width = rendition.Width,
            Height = rendition.Height,
            Format = rendition.HasAlpha ? AVPixelFormat.AV_PIX_FMT_RGBA : AVPixelFormat.AV_PIX_FMT_RGB24,
            Planes = [samples.Share()],
            Strides = [rendition.Width * (rendition.HasAlpha ? 4 : 3)],
            Primaries = (AVColorPrimaries)ColorMath.CicpPrimaries(rendition.Primaries),
            Transfer = AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1,
            Matrix = AVColorSpace.AVCOL_SPC_RGB,
        };
        samples.Dispose();
        output.Write(FfmpegStillEncoder.Encode(EncoderName, picture, Options(quality, effort)));
    }

    /// <summary>Maps quality to a Butteraugli distance the way cjxl does; 100 is lossless.</summary>
    private static Dictionary<string, string> Options(int quality, int effort)
    {
        int q = Math.Clamp(quality, 1, 100);
        double distance = q >= 100 ? 0
            : q >= 30 ? 0.1 + (100 - q) * 0.09
            : 6.4 + Math.Pow(2.5, (30 - q) / 5.0) / 6.25;
        return new Dictionary<string, string>
        {
            ["distance"] = distance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            ["effort"] = Math.Clamp(effort, 1, 9).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
