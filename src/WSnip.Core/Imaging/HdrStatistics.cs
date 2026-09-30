namespace WSnip.Core.Imaging;

/// <summary>Luminance and gamut measurements of a relative HDR image (one unit is SDR white).</summary>
public sealed record HdrStatistics(
    float PeakComponent,
    float RobustPeak,
    float PeakLuminance,
    long HdrPixels,
    long OutsideSrgbPixels,
    long OutsideDisplayP3Pixels,
    long VisiblePixels)
{
    /// <summary>Values this far above SDR white count as highlights rather than rounding noise.</summary>
    public const float HdrThreshold = 1.03f;

    private const float GamutTolerance = -1f / 256;
    private const int HistogramBins = 1024;
    private const float HistogramStops = 10;

    public bool HasHdr => HdrPixels >= 16;

    public bool HasWideGamut => OutsideSrgbPixels >= 16;

    /// <summary>Primaries wide enough for the SDR base of this image.</summary>
    public ColorPrimaries SuggestedBasePrimaries => HasWideGamut ? ColorPrimaries.DisplayP3 : ColorPrimaries.Bt709;

    public static HdrStatistics Measure(HdrImage image)
    {
        float[] toP3 = ColorMath.Matrix(ColorPrimaries.Bt709, ColorPrimaries.DisplayP3);
        (float lr, float lg, float lb) = ColorMath.Luminance(ColorPrimaries.Bt709);
        object gate = new();
        var total = new Accumulator();

        int bands = Math.Min(Environment.ProcessorCount, Math.Max(1, image.Height / 32));
        Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = bands }, band =>
        {
            var local = new Accumulator();
            int first = (int)((long)image.Height * band / bands);
            int last = (int)((long)image.Height * (band + 1) / bands);
            for (int y = first; y < last; y++)
            {
                ReadOnlySpan<Half> row = image.ReadRow(y);
                for (int i = 0; i < row.Length; i += 4)
                {
                    if ((float)row[i + 3] <= 0)
                        continue;

                    float r = (float)row[i], g = (float)row[i + 1], b = (float)row[i + 2];
                    local.Visible++;
                    float m = MathF.Max(r, MathF.Max(g, b));
                    if (m > local.Peak)
                        local.Peak = m;
                    float luminance = lr * r + lg * g + lb * b;
                    if (luminance > local.PeakLuminance)
                        local.PeakLuminance = luminance;
                    if (m > HdrThreshold)
                        local.Hdr++;
                    if (m > 1)
                    {
                        int bin = (int)(MathF.Log2(m) * (HistogramBins / HistogramStops));
                        local.Histogram[Math.Min(bin, HistogramBins - 1)]++;
                    }

                    if (MathF.Min(r, MathF.Min(g, b)) < GamutTolerance * MathF.Max(1, m))
                    {
                        local.OutsideSrgb++;
                        float pr = toP3[0] * r + toP3[1] * g + toP3[2] * b;
                        float pg = toP3[3] * r + toP3[4] * g + toP3[5] * b;
                        float pb = toP3[6] * r + toP3[7] * g + toP3[8] * b;
                        if (MathF.Min(pr, MathF.Min(pg, pb)) < GamutTolerance * MathF.Max(1, m))
                            local.OutsideP3++;
                    }
                }
            }

            lock (gate)
                total.Add(local);
        });

        return new HdrStatistics(
            MathF.Max(total.Peak, 0),
            ComputeRobustPeak(total),
            MathF.Max(total.PeakLuminance, 0),
            total.Hdr,
            total.OutsideSrgb,
            total.OutsideP3,
            total.Visible);
    }

    private static float ComputeRobustPeak(Accumulator total)
    {
        if (total.Peak <= 1)
            return MathF.Max(total.Peak, 0);

        // Ignores a handful of hot pixels so an isolated outlier does not flatten the tone curve.
        long allowance = Math.Max(16, total.Visible / 10000);
        long above = 0;
        for (int bin = HistogramBins - 1; bin >= 0; bin--)
        {
            above += total.Histogram[bin];
            if (above > allowance)
                return MathF.Min(total.Peak, MathF.Pow(2, (bin + 1) * (HistogramStops / HistogramBins)));
        }

        return 1;
    }

    private sealed class Accumulator
    {
        public readonly long[] Histogram = new long[HistogramBins];
        public float Peak = float.MinValue;
        public float PeakLuminance = float.MinValue;
        public long Hdr;
        public long OutsideSrgb;
        public long OutsideP3;
        public long Visible;

        public void Add(Accumulator other)
        {
            for (int i = 0; i < Histogram.Length; i++)
                Histogram[i] += other.Histogram[i];
            Peak = MathF.Max(Peak, other.Peak);
            PeakLuminance = MathF.Max(PeakLuminance, other.PeakLuminance);
            Hdr += other.Hdr;
            OutsideSrgb += other.OutsideSrgb;
            OutsideP3 += other.OutsideP3;
            Visible += other.Visible;
        }
    }
}
