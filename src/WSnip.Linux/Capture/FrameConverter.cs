using WSnip.Core.Imaging;
using WSnip.Linux.Interop;

namespace WSnip.Linux.Capture;

/// <summary>Turns 8-bit sRGB frames into the linear scRGB images the capture pipeline works in.</summary>
internal static class FrameConverter
{
    private static readonly Half[] Linear = Enumerable.Range(0, 256).Select(code => (Half)ColorMath.SrgbByteToLinear((byte)code)).ToArray();

    /// <param name="keepAlpha">
    /// Reads alpha, which compositors deliver premultiplied, into straight alpha; otherwise the
    /// frame is opaque.
    /// </param>
    public static HdrImage ToLinear(PipeWireFrame frame, bool keepAlpha)
    {
        (int red, int green, int blue, int alpha) = Layout(frame.Info.Format);
        if (!keepAlpha)
            alpha = -1;
        int width = frame.Info.Width;
        byte[] pixels = frame.Pixels;
        var image = new HdrImage(width, frame.Info.Height);
        Parallel.For(0, image.Height, y =>
        {
            ReadOnlySpan<byte> source = pixels.AsSpan(y * width * 4, width * 4);
            Span<Half> target = image.Row(y);
            for (int x = 0; x < width; x++)
            {
                ReadOnlySpan<byte> pixel = source.Slice(x * 4, 4);
                Span<Half> sample = target.Slice(x * 4, 4);
                int opacity = alpha < 0 ? 255 : pixel[alpha];
                if (opacity == 255)
                {
                    sample[0] = Linear[pixel[red]];
                    sample[1] = Linear[pixel[green]];
                    sample[2] = Linear[pixel[blue]];
                    sample[3] = Half.One;
                }
                else if (opacity == 0)
                {
                    sample.Clear();
                }
                else
                {
                    sample[0] = Linear[Unpremultiply(pixel[red], opacity)];
                    sample[1] = Linear[Unpremultiply(pixel[green], opacity)];
                    sample[2] = Linear[Unpremultiply(pixel[blue], opacity)];
                    sample[3] = (Half)(opacity / 255f);
                }
            }
        });
        return image;
    }

    private static int Unpremultiply(int code, int alpha) => Math.Min(255, (code * 255 + alpha / 2) / alpha);

    /// <summary>Byte offsets of red, green, blue and alpha in a pixel; alpha is -1 when the format has none.</summary>
    private static (int Red, int Green, int Blue, int Alpha) Layout(SpaVideoFormat format) => format switch
    {
        SpaVideoFormat.RGBx => (0, 1, 2, -1),
        SpaVideoFormat.RGBA => (0, 1, 2, 3),
        SpaVideoFormat.BGRx => (2, 1, 0, -1),
        SpaVideoFormat.BGRA => (2, 1, 0, 3),
        SpaVideoFormat.xRGB => (1, 2, 3, -1),
        SpaVideoFormat.ARGB => (1, 2, 3, 0),
        SpaVideoFormat.xBGR => (3, 2, 1, -1),
        SpaVideoFormat.ABGR => (3, 2, 1, 0),
        _ => throw new NotSupportedException($"The pixel format {format} is not supported."),
    };
}
