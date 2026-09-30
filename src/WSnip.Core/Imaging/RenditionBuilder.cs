namespace WSnip.Core.Imaging;

/// <summary>How highlights above SDR white are brought into the SDR rendition.</summary>
public enum SdrToneMapping
{
    /// <summary>
    /// Compresses highlights only around HDR content, so SDR areas such as desktop UI keep their
    /// exact pixel values while HDR video, games and photos roll off smoothly.
    /// </summary>
    Adaptive,

    /// <summary>Applies the same highlight roll-off to the whole image.</summary>
    Global,

    /// <summary>Clips highlights at SDR white, preserving hue.</summary>
    Clip,
}

public sealed record RenditionOptions
{
    public SdrToneMapping ToneMapping { get; init; } = SdrToneMapping.Adaptive;

    /// <summary>Primaries of the SDR base; null picks sRGB unless the image needs a wider gamut.</summary>
    public ColorPrimaries? BasePrimaries { get; init; }

    /// <summary>Composites transparent areas over white, for formats written without alpha.</summary>
    public bool FlattenAlpha { get; init; }

    public bool BuildGainMap { get; init; } = true;

    /// <summary>Downscale factor of the gain map; 1 keeps sharp HDR edges next to SDR content.</summary>
    public int GainMapScale { get; init; } = 1;

    public bool MultichannelGainMap { get; init; } = true;
}

/// <summary>An 8-bit gain map image with its metadata.</summary>
public sealed class GainMapImage
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>1 for a luminance gain map, 3 for RGB.</summary>
    public required int Channels { get; init; }

    public required byte[] Pixels { get; init; }

    public required GainMapMetadata Metadata { get; init; }
}

/// <summary>The SDR base of a snip and, when it holds HDR content, the gain map recovering it.</summary>
public sealed class Rendition
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required ColorPrimaries Primaries { get; init; }

    /// <summary>Straight-alpha RGBA with the sRGB transfer function on <see cref="Primaries"/>.</summary>
    public required byte[] Sdr { get; init; }

    public required bool HasAlpha { get; init; }

    public GainMapImage? GainMap { get; init; }

    /// <summary>Content peak relative to SDR white.</summary>
    public required float Peak { get; init; }
}

/// <summary>Builds SDR renditions and ISO 21496-1 gain maps from relative HDR images.</summary>
public static class RenditionBuilder
{
    private const float GainOffset = 1f / 64;
    private const int BlockSize = 16;

    public static Rendition Build(HdrImage image, HdrStatistics statistics, RenditionOptions options)
    {
        ColorPrimaries primaries = options.BasePrimaries ?? statistics.SuggestedBasePrimaries;
        bool flatten = options.FlattenAlpha;
        bool hasAlpha = !flatten && image.HasTransparency();
        var context = new PixelContext(primaries, flatten);
        ToneCurve curve = ToneCurve.Create(statistics.HasHdr ? statistics.RobustPeak : 1);
        float[]? weights = options.ToneMapping == SdrToneMapping.Adaptive && curve.IsActive
            ? BuildWeights(image, context)
            : null;
        float globalWeight = options.ToneMapping switch
        {
            SdrToneMapping.Global => 1,
            SdrToneMapping.Clip => 0,
            _ => 0,
        };

        int width = image.Width, height = image.Height;
        int blocksX = (width + BlockSize - 1) / BlockSize, blocksY = (height + BlockSize - 1) / BlockSize;
        var sdr = new byte[width * height * 4];
        bool gainMap = options.BuildGainMap && statistics.HasHdr;
        int channels = options.MultichannelGainMap ? 3 : 1;
        var minimum = new float[] { float.MaxValue, float.MaxValue, float.MaxValue };
        var maximum = new float[] { float.MinValue, float.MinValue, float.MinValue };
        object gate = new();

        ParallelBands(height, (first, last) =>
        {
            Span<float> localMin = [float.MaxValue, float.MaxValue, float.MaxValue];
            Span<float> localMax = [float.MinValue, float.MinValue, float.MinValue];
            for (int y = first; y < last; y++)
            {
                ReadOnlySpan<Half> row = image.ReadRow(y);
                Span<byte> target = sdr.AsSpan(y * width * 4, width * 4);
                for (int x = 0; x < width; x++)
                {
                    float alpha = context.Hdr(row, x, out float r, out float g, out float b);
                    float weight = weights is null ? globalWeight : SampleWeight(weights, blocksX, blocksY, x, y);
                    curve.Map(ref r, ref g, ref b, weight, out float sr, out float sg, out float sb);
                    byte er = ColorMath.LinearToSrgbByte(sr);
                    byte eg = ColorMath.LinearToSrgbByte(sg);
                    byte eb = ColorMath.LinearToSrgbByte(sb);
                    target[x * 4] = er;
                    target[x * 4 + 1] = eg;
                    target[x * 4 + 2] = eb;
                    target[x * 4 + 3] = hasAlpha ? (byte)Math.Clamp((int)(alpha * 255 + 0.5f), 0, 255) : (byte)255;
                    if (!gainMap)
                        continue;

                    GainOf(r, g, b, er, eg, eb, channels, out float g0, out float g1, out float g2);
                    localMin[0] = MathF.Min(localMin[0], g0);
                    localMax[0] = MathF.Max(localMax[0], g0);
                    if (channels == 3)
                    {
                        localMin[1] = MathF.Min(localMin[1], g1);
                        localMax[1] = MathF.Max(localMax[1], g1);
                        localMin[2] = MathF.Min(localMin[2], g2);
                        localMax[2] = MathF.Max(localMax[2], g2);
                    }
                }
            }

            lock (gate)
            {
                for (int c = 0; c < 3; c++)
                {
                    minimum[c] = MathF.Min(minimum[c], localMin[c]);
                    maximum[c] = MathF.Max(maximum[c], localMax[c]);
                }
            }
        });

        GainMapImage? map = null;
        if (gainMap)
        {
            if (channels == 1)
            {
                minimum[1] = minimum[2] = minimum[0];
                maximum[1] = maximum[2] = maximum[0];
            }

            float headroom = MathF.Max(maximum[0], MathF.Max(maximum[1], maximum[2]));
            if (headroom > 1f / 64)
            {
                for (int c = 0; c < 3; c++)
                {
                    minimum[c] = MathF.Min(minimum[c], 0);
                    if (maximum[c] - minimum[c] < 1f / 256)
                        maximum[c] = minimum[c] + 1f / 256;
                }

                var metadata = new GainMapMetadata(minimum, maximum, [1, 1, 1],
                    [GainOffset, GainOffset, GainOffset], [GainOffset, GainOffset, GainOffset],
                    0, headroom);
                map = EncodeGainMap(image, context, sdr, metadata, channels, Math.Clamp(options.GainMapScale, 1, 8));
            }
        }

        return new Rendition
        {
            Width = width,
            Height = height,
            Primaries = primaries,
            Sdr = sdr,
            HasAlpha = hasAlpha,
            GainMap = map,
            Peak = statistics.PeakComponent,
        };
    }

    private static GainMapImage EncodeGainMap(HdrImage image, PixelContext context, byte[] sdr,
        GainMapMetadata metadata, int channels, int scale)
    {
        int width = image.Width, height = image.Height;
        int mapWidth = Math.Max(1, (width + scale - 1) / scale);
        int mapHeight = Math.Max(1, (height + scale - 1) / scale);
        var pixels = new byte[mapWidth * mapHeight * channels];
        float[] min = metadata.GainMapMin, max = metadata.GainMapMax;

        ParallelBands(mapHeight, (first, last) =>
        {
            for (int my = first; my < last; my++)
            {
                for (int mx = 0; mx < mapWidth; mx++)
                {
                    float hr = 0, hg = 0, hb = 0, sr = 0, sg = 0, sb = 0;
                    int count = 0;
                    for (int y = my * scale; y < Math.Min(height, (my + 1) * scale); y++)
                    {
                        ReadOnlySpan<Half> row = image.ReadRow(y);
                        for (int x = mx * scale; x < Math.Min(width, (mx + 1) * scale); x++)
                        {
                            context.Hdr(row, x, out float r, out float g, out float b);
                            int index = (y * width + x) * 4;
                            hr += r; hg += g; hb += b;
                            sr += ColorMath.SrgbByteToLinear(sdr[index]);
                            sg += ColorMath.SrgbByteToLinear(sdr[index + 1]);
                            sb += ColorMath.SrgbByteToLinear(sdr[index + 2]);
                            count++;
                        }
                    }

                    float inverse = 1f / count;
                    hr *= inverse; hg *= inverse; hb *= inverse;
                    sr *= inverse; sg *= inverse; sb *= inverse;
                    int target = (my * mapWidth + mx) * channels;
                    if (channels == 3)
                    {
                        pixels[target] = Quantize(Log2Gain(hr, sr), min[0], max[0]);
                        pixels[target + 1] = Quantize(Log2Gain(hg, sg), min[1], max[1]);
                        pixels[target + 2] = Quantize(Log2Gain(hb, sb), min[2], max[2]);
                    }
                    else
                    {
                        pixels[target] = Quantize(Log2Gain(MathF.Max(hr, MathF.Max(hg, hb)),
                            MathF.Max(sr, MathF.Max(sg, sb))), min[0], max[0]);
                    }
                }
            }
        });

        return new GainMapImage
        {
            Width = mapWidth,
            Height = mapHeight,
            Channels = channels,
            Pixels = pixels,
            Metadata = metadata,
        };
    }

    private static void GainOf(float r, float g, float b, byte er, byte eg, byte eb, int channels,
        out float g0, out float g1, out float g2)
    {
        float sr = ColorMath.SrgbByteToLinear(er);
        float sg = ColorMath.SrgbByteToLinear(eg);
        float sb = ColorMath.SrgbByteToLinear(eb);
        if (channels == 3)
        {
            g0 = Log2Gain(r, sr);
            g1 = Log2Gain(g, sg);
            g2 = Log2Gain(b, sb);
        }
        else
        {
            g0 = Log2Gain(MathF.Max(r, MathF.Max(g, b)), MathF.Max(sr, MathF.Max(sg, sb)));
            g1 = g2 = g0;
        }
    }

    private static float Log2Gain(float hdr, float sdr) => MathF.Log2((hdr + GainOffset) / (sdr + GainOffset));

    private static byte Quantize(float gain, float minimum, float maximum)
    {
        float recovery = Math.Clamp((gain - minimum) / (maximum - minimum), 0, 1);
        return (byte)(recovery * 255 + 0.5f);
    }

    /// <summary>
    /// Marks 16-pixel blocks that contain highlights, grows the marked area and feathers it, so
    /// that the highlight roll-off fades out over roughly a hundred pixels around HDR content.
    /// </summary>
    private static float[] BuildWeights(HdrImage image, PixelContext context)
    {
        int blocksX = (image.Width + BlockSize - 1) / BlockSize;
        int blocksY = (image.Height + BlockSize - 1) / BlockSize;
        var marked = new float[blocksX * blocksY];
        ParallelBands(blocksY, (first, last) =>
        {
            for (int by = first; by < last; by++)
            {
                for (int y = by * BlockSize; y < Math.Min(image.Height, (by + 1) * BlockSize); y++)
                {
                    ReadOnlySpan<Half> row = image.ReadRow(y);
                    for (int x = 0; x < image.Width; x++)
                    {
                        context.Hdr(row, x, out float r, out float g, out float b);
                        if (MathF.Max(r, MathF.Max(g, b)) > HdrStatistics.HdrThreshold)
                            marked[by * blocksX + x / BlockSize] = 1;
                    }
                }
            }
        });

        float[] grown = Dilate(marked, blocksX, blocksY, 3);
        float[] blurred = BoxBlur(BoxBlur(grown, blocksX, blocksY, 3), blocksX, blocksY, 3);
        for (int i = 0; i < blurred.Length; i++)
            blurred[i] = MathF.Min(1, blurred[i] * 2);
        return blurred;
    }

    private static float SampleWeight(float[] weights, int blocksX, int blocksY, int x, int y)
    {
        float fx = Math.Clamp((x + 0.5f) / BlockSize - 0.5f, 0, blocksX - 1);
        float fy = Math.Clamp((y + 0.5f) / BlockSize - 0.5f, 0, blocksY - 1);
        int x0 = (int)fx, y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, blocksX - 1), y1 = Math.Min(y0 + 1, blocksY - 1);
        float tx = fx - x0, ty = fy - y0;
        float top = weights[y0 * blocksX + x0] + (weights[y0 * blocksX + x1] - weights[y0 * blocksX + x0]) * tx;
        float bottom = weights[y1 * blocksX + x0] + (weights[y1 * blocksX + x1] - weights[y1 * blocksX + x0]) * tx;
        return top + (bottom - top) * ty;
    }

    private static float[] Dilate(float[] source, int width, int height, int radius)
    {
        var horizontal = new float[source.Length];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float value = 0;
            for (int dx = -radius; dx <= radius && value == 0; dx++)
            {
                int sx = x + dx;
                if (sx >= 0 && sx < width)
                    value = MathF.Max(value, source[y * width + sx]);
            }

            horizontal[y * width + x] = value;
        }

        var result = new float[source.Length];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float value = 0;
            for (int dy = -radius; dy <= radius && value == 0; dy++)
            {
                int sy = y + dy;
                if (sy >= 0 && sy < height)
                    value = MathF.Max(value, horizontal[sy * width + x]);
            }

            result[y * width + x] = value;
        }

        return result;
    }

    private static float[] BoxBlur(float[] source, int width, int height, int radius)
    {
        var horizontal = new float[source.Length];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float sum = 0;
            for (int dx = -radius; dx <= radius; dx++)
                sum += source[y * width + Math.Clamp(x + dx, 0, width - 1)];
            horizontal[y * width + x] = sum / (2 * radius + 1);
        }

        var result = new float[source.Length];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float sum = 0;
            for (int dy = -radius; dy <= radius; dy++)
                sum += horizontal[Math.Clamp(y + dy, 0, height - 1) * width + x];
            result[y * width + x] = sum / (2 * radius + 1);
        }

        return result;
    }

    private static void ParallelBands(int count, Action<int, int> band)
    {
        int workers = Math.Min(Environment.ProcessorCount, Math.Max(1, count / 16));
        if (workers <= 1)
        {
            band(0, count);
            return;
        }

        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers },
            worker => band((int)((long)count * worker / workers), (int)((long)count * (worker + 1) / workers)));
    }

    /// <summary>Converts source pixels into the base primaries, inside that gamut, over white if flattened.</summary>
    private sealed class PixelContext(ColorPrimaries primaries, bool flatten)
    {
        private readonly float[] matrix = ColorMath.Matrix(ColorPrimaries.Bt709, primaries);
        private readonly (float R, float G, float B) luminance = ColorMath.Luminance(primaries);

        public float Hdr(ReadOnlySpan<Half> row, int x, out float r, out float g, out float b)
        {
            float sr = (float)row[x * 4], sg = (float)row[x * 4 + 1], sb = (float)row[x * 4 + 2];
            float alpha = Math.Clamp((float)row[x * 4 + 3], 0, 1);
            r = matrix[0] * sr + matrix[1] * sg + matrix[2] * sb;
            g = matrix[3] * sr + matrix[4] * sg + matrix[5] * sb;
            b = matrix[6] * sr + matrix[7] * sg + matrix[8] * sb;

            float lowest = MathF.Min(r, MathF.Min(g, b));
            if (lowest < 0)
            {
                // Desaturates toward the pixel's own luminance until no component is negative.
                float y = luminance.R * r + luminance.G * g + luminance.B * b;
                if (y <= 0)
                {
                    r = g = b = 0;
                }
                else
                {
                    float t = y / (y - lowest);
                    r = MathF.Max(0, y + (r - y) * t);
                    g = MathF.Max(0, y + (g - y) * t);
                    b = MathF.Max(0, y + (b - y) * t);
                }
            }

            if (flatten && alpha < 1)
            {
                r = r * alpha + (1 - alpha);
                g = g * alpha + (1 - alpha);
                b = b * alpha + (1 - alpha);
                return 1;
            }

            return alpha;
        }
    }

    /// <summary>
    /// Maps the largest component through a Mobius shoulder that is the identity up to a knee and
    /// reaches SDR white at the content peak, scaling all components together to keep hue.
    /// </summary>
    internal readonly struct ToneCurve
    {
        private readonly float knee;
        private readonly float peak;
        private readonly float a;
        private readonly float b;
        private readonly float scale;

        private ToneCurve(float knee, float peak)
        {
            this.knee = knee;
            this.peak = peak;
            if (!(peak > 1) || !(knee < 1))
            {
                a = b = scale = 0;
                return;
            }

            float j = knee;
            a = -j * j * (peak - 1) / (j * j - 2 * j + peak);
            b = (j * j - 2 * j * peak + peak) / MathF.Max(1e-6f, peak - 1);
            scale = (b * b + 2 * b * j + j * j) / (b - a);
        }

        public bool IsActive => peak > 1;

        public static ToneCurve Create(float peak)
        {
            if (!(peak > HdrStatistics.HdrThreshold))
                return new ToneCurve(1, 1);

            float knee = Math.Clamp(1 - 0.2f * MathF.Log2(peak), 0.6f, 0.9f);
            return new ToneCurve(knee, peak);
        }

        public void Map(ref float r, ref float g, ref float b, float weight, out float sr, out float sg, out float sb)
        {
            float m = MathF.Max(r, MathF.Max(g, b));
            if (m <= knee || m <= 0)
            {
                sr = MathF.Min(r, 1);
                sg = MathF.Min(g, 1);
                sb = MathF.Min(b, 1);
                return;
            }

            float clipped = MathF.Min(m, 1);
            float target = clipped;
            if (IsActive && weight > 0)
            {
                float shoulder = m >= peak ? 1 : MathF.Min(1, scale * (m + a) / (m + this.b));
                target = clipped + (shoulder - clipped) * weight;
            }

            float factor = target / m;
            sr = r * factor;
            sg = g * factor;
            sb = b * factor;
        }
    }
}
