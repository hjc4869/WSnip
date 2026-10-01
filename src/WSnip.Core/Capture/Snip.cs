using WSnip.Core.Imaging;

namespace WSnip.Core.Capture;

/// <summary>A captured region, normalized so that one unit is diffuse (SDR) white.</summary>
public sealed class Snip
{
    public Snip(HdrImage image, DateTimeOffset capturedAt, double sdrWhiteNits, double? displayPeakNits)
    {
        Image = image;
        CapturedAt = capturedAt;
        SourceSdrWhiteNits = sdrWhiteNits;
        SourceDisplayPeakNits = displayPeakNits;
        Statistics = HdrStatistics.Measure(image);
    }

    /// <summary>Relative linear scRGB with straight alpha.</summary>
    public HdrImage Image { get; }

    public DateTimeOffset CapturedAt { get; }

    /// <summary>SDR white of the display the snip came from, for information only.</summary>
    public double SourceSdrWhiteNits { get; }

    public double? SourceDisplayPeakNits { get; }

    public HdrStatistics Statistics { get; }

    /// <summary>Tone mapping tuned for this snip; null uses the settings' default.</summary>
    public ToneMapSettings? ToneMap { get; init; }

    public int Width => Image.Width;

    public int Height => Image.Height;
}

/// <summary>Builds snips from a frozen screen snapshot.</summary>
public static class SnipComposer
{
    /// <summary>
    /// Copies a virtual-desktop rectangle out of the snapshot, dividing each display's pixels by its
    /// own SDR white so that displays with different brightness settings join seamlessly. Areas no
    /// display covers, and areas outside an optional freeform outline, become transparent.
    /// </summary>
    public static Snip Compose(ScreenSnapshot snapshot, PixelRect region, IReadOnlyList<(double X, double Y)>? outline = null)
    {
        region = region.Intersect(snapshot.VirtualBounds);
        if (region.IsEmpty)
            throw new ArgumentException("The selected region does not intersect any display.", nameof(region));

        var image = new HdrImage(region.Width, region.Height);
        image.Fill(0, 0, 0, 0);

        MonitorCapture? dominant = null;
        long dominantArea = -1;
        foreach (MonitorCapture monitor in snapshot.Monitors)
        {
            PixelRect overlap = monitor.Bounds.Intersect(region);
            if (overlap.IsEmpty)
                continue;

            long area = (long)overlap.Width * overlap.Height;
            if (area > dominantArea)
            {
                dominantArea = area;
                dominant = monitor;
            }

            float scale = (float)(1 / monitor.Color.WhiteScale);
            int sourceX = overlap.X - monitor.Bounds.X;
            int sourceY = overlap.Y - monitor.Bounds.Y;
            int targetX = overlap.X - region.X;
            int targetY = overlap.Y - region.Y;
            ParallelRows.For(overlap.Height, row =>
            {
                ReadOnlySpan<Half> source = monitor.Image.ReadRow(sourceY + row)
                    .Slice(sourceX * HdrImage.Channels, overlap.Width * HdrImage.Channels);
                Span<Half> target = image.Row(targetY + row)
                    .Slice(targetX * HdrImage.Channels, overlap.Width * HdrImage.Channels);
                for (int i = 0; i < source.Length; i += 4)
                {
                    target[i] = (Half)((float)source[i] * scale);
                    target[i + 1] = (Half)((float)source[i + 1] * scale);
                    target[i + 2] = (Half)((float)source[i + 2] * scale);
                    target[i + 3] = Half.One;
                }
            });
        }

        if (outline is { Count: >= 3 })
            ApplyOutline(image, region, outline);

        DisplayColorInfo color = dominant?.Color ?? DisplayColorInfo.Sdr;
        return new Snip(image, snapshot.CapturedAt,
            color.HdrActive ? color.SdrWhiteNits : ColorMath.ReferenceWhiteNits,
            color.HdrActive ? color.MaxLuminanceNits : null);
    }

    /// <summary>
    /// Normalizes a captured window to relative light by the SDR white of its display, keeping the
    /// window's own transparency. The capture's pixels are rescaled in place and become the snip's.
    /// </summary>
    public static Snip FromWindow(WindowCapture capture)
    {
        HdrImage image = capture.Image;
        image.ScaleColor((float)(1 / capture.Color.WhiteScale));
        DisplayColorInfo color = capture.Color;
        return new Snip(image, capture.CapturedAt,
            color.HdrActive ? color.SdrWhiteNits : ColorMath.ReferenceWhiteNits,
            color.HdrActive ? color.MaxLuminanceNits : null);
    }

    private static void ApplyOutline(HdrImage image, PixelRect region, IReadOnlyList<(double X, double Y)> outline)
    {
        byte[] mask = OutlineMask.Rasterize(region, outline);
        ParallelRows.For(image.Height, y =>
        {
            Span<Half> row = image.Row(y);
            int offset = y * image.Width;
            for (int x = 0; x < image.Width; x++)
            {
                float coverage = mask[offset + x] / 255f;
                row[x * 4 + 3] = (Half)((float)row[x * 4 + 3] * coverage);
            }
        });
    }
}
