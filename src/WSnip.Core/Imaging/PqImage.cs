namespace WSnip.Core.Imaging;

/// <summary>16-bit BT.2100 PQ pixels, for formats that store HDR directly rather than as a gain map.</summary>
public sealed class PqImage
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Interleaved R, G, B, A samples; alpha is linear and straight.</summary>
    public required ushort[] Pixels { get; init; }

    public required bool HasAlpha { get; init; }

    public required ColorPrimaries Primaries { get; init; }

    /// <summary>Largest component anywhere in the image, in nits.</summary>
    public required float MaxContentLightLevel { get; init; }

    /// <summary>Frame average of the largest component, in nits.</summary>
    public required float MaxFrameAverageLightLevel { get; init; }
}

public static class PqImageBuilder
{
    /// <summary>
    /// Converts a relative image to absolute PQ, placing SDR white at <paramref name="referenceWhiteNits"/>
    /// (203 nits by BT.2408) so the image keeps its appearance on any HDR display.
    /// </summary>
    public static PqImage Build(HdrImage image, bool flattenAlpha, ColorPrimaries primaries = ColorPrimaries.Bt2020,
        double referenceWhiteNits = ColorMath.ReferenceWhiteNits)
    {
        float[] matrix = ColorMath.Matrix(ColorPrimaries.Bt709, primaries);
        float white = (float)referenceWhiteNits;
        bool hasAlpha = !flattenAlpha && image.HasTransparency();
        var pixels = new ushort[image.Width * image.Height * 4];
        var rowPeaks = new float[image.Height];
        var rowSums = new double[image.Height];

        ParallelRows.For(image.Height, y =>
        {
            ReadOnlySpan<Half> row = image.ReadRow(y);
            Span<ushort> target = pixels.AsSpan(y * image.Width * 4, image.Width * 4);
            float peak = 0;
            double sum = 0;
            for (int x = 0; x < image.Width; x++)
            {
                float r = (float)row[x * 4], g = (float)row[x * 4 + 1], b = (float)row[x * 4 + 2];
                float alpha = Math.Clamp((float)row[x * 4 + 3], 0, 1);
                if (flattenAlpha && alpha < 1)
                {
                    r = r * alpha + (1 - alpha);
                    g = g * alpha + (1 - alpha);
                    b = b * alpha + (1 - alpha);
                    alpha = 1;
                }

                float cr = MathF.Max(0, matrix[0] * r + matrix[1] * g + matrix[2] * b) * white;
                float cg = MathF.Max(0, matrix[3] * r + matrix[4] * g + matrix[5] * b) * white;
                float cb = MathF.Max(0, matrix[6] * r + matrix[7] * g + matrix[8] * b) * white;
                float m = MathF.Max(cr, MathF.Max(cg, cb));
                peak = MathF.Max(peak, m);
                sum += m;
                target[x * 4] = Quantize(ColorMath.NitsToPq(cr));
                target[x * 4 + 1] = Quantize(ColorMath.NitsToPq(cg));
                target[x * 4 + 2] = Quantize(ColorMath.NitsToPq(cb));
                target[x * 4 + 3] = hasAlpha ? Quantize(alpha) : ushort.MaxValue;
            }

            rowPeaks[y] = peak;
            rowSums[y] = sum;
        });

        return new PqImage
        {
            Width = image.Width,
            Height = image.Height,
            Pixels = pixels,
            HasAlpha = hasAlpha,
            Primaries = primaries,
            MaxContentLightLevel = MathF.Min(rowPeaks.Max(), (float)ColorMath.PqMaximumNits),
            MaxFrameAverageLightLevel = (float)Math.Min(rowSums.Sum() / ((double)image.Width * image.Height), ColorMath.PqMaximumNits),
        };
    }

    private static ushort Quantize(float value) => (ushort)Math.Clamp((int)(value * 65535 + 0.5f), 0, 65535);
}
