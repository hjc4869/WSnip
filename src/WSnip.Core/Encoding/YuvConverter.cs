namespace WSnip.Core.Encoding;

/// <summary>YCbCr matrices used when handing RGB pictures to video encoders.</summary>
internal enum YuvMatrix
{
    Bt709,
    Bt601,
}

/// <summary>Converts 8-bit RGB into full-range planar YCbCr or GBR.</summary>
internal static class YuvConverter
{
    /// <summary>Converts RGBA (alpha ignored) to 4:4:4 or 4:2:0 planes.</summary>
    public static (byte[][] Planes, int[] Strides) FromRgba(byte[] rgba, int width, int height, bool subsample, YuvMatrix matrix)
    {
        (float kr, float kb) = matrix == YuvMatrix.Bt709 ? (0.2126f, 0.0722f) : (0.299f, 0.114f);
        float kg = 1 - kr - kb;
        float cbScale = 1 / (2 * (1 - kb));
        float crScale = 1 / (2 * (1 - kr));
        int chromaWidth = subsample ? (width + 1) / 2 : width;
        int chromaHeight = subsample ? (height + 1) / 2 : height;
        var y = new byte[width * height];
        var cb = new byte[chromaWidth * chromaHeight];
        var cr = new byte[chromaWidth * chromaHeight];

        Parallel.For(0, height, row =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = (row * width + x) * 4;
                y[row * width + x] = Clamp(kr * rgba[i] + kg * rgba[i + 1] + kb * rgba[i + 2]);
            }
        });

        Parallel.For(0, chromaHeight, row =>
        {
            for (int x = 0; x < chromaWidth; x++)
            {
                float r = 0, g = 0, b = 0;
                int count = 0;
                int step = subsample ? 2 : 1;
                for (int dy = 0; dy < step; dy++)
                {
                    int sy = Math.Min(row * step + dy, height - 1);
                    for (int dx = 0; dx < step; dx++)
                    {
                        int sx = Math.Min(x * step + dx, width - 1);
                        int i = (sy * width + sx) * 4;
                        r += rgba[i];
                        g += rgba[i + 1];
                        b += rgba[i + 2];
                        count++;
                    }
                }

                r /= count; g /= count; b /= count;
                float luma = kr * r + kg * g + kb * b;
                cb[row * chromaWidth + x] = Clamp((b - luma) * cbScale + 128);
                cr[row * chromaWidth + x] = Clamp((r - luma) * crScale + 128);
            }
        });

        return ([y, cb, cr], [width, chromaWidth, chromaWidth]);
    }

    /// <summary>Splits interleaved RGB into G, B, R planes for the identity (GBR) matrix.</summary>
    public static (byte[][] Planes, int[] Strides) GbrFromRgb(byte[] rgb, int width, int height)
    {
        var g = new byte[width * height];
        var b = new byte[width * height];
        var r = new byte[width * height];
        for (int i = 0, p = 0; p < g.Length; i += 3, p++)
        {
            r[p] = rgb[i];
            g[p] = rgb[i + 1];
            b[p] = rgb[i + 2];
        }

        return ([g, b, r], [width, width, width]);
    }

    /// <summary>Expands interleaved RGB or single-channel samples to RGBA.</summary>
    public static byte[] ToRgba(byte[] samples, int width, int height, int channels)
    {
        var rgba = new byte[width * height * 4];
        for (int p = 0; p < width * height; p++)
        {
            if (channels == 3)
            {
                rgba[p * 4] = samples[p * 3];
                rgba[p * 4 + 1] = samples[p * 3 + 1];
                rgba[p * 4 + 2] = samples[p * 3 + 2];
            }
            else
            {
                rgba[p * 4] = rgba[p * 4 + 1] = rgba[p * 4 + 2] = samples[p];
            }

            rgba[p * 4 + 3] = 255;
        }

        return rgba;
    }

    private static byte Clamp(float value) => (byte)Math.Clamp((int)(value + 0.5f), 0, 255);
}
