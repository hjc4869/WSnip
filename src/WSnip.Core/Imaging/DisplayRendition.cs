namespace WSnip.Core.Imaging;

/// <summary>Prepares relative HDR images for a display with limited headroom.</summary>
public static class DisplayRendition
{
    private static readonly ToneMapSettings HeadroomCurve = new() { Curve = ToneMapCurve.Bt2390 };

    /// <summary>
    /// Returns the image unchanged when its peak fits the display headroom, or a copy whose
    /// highlights roll off hue-preservingly into it. Values stay relative to SDR white.
    /// </summary>
    public static HdrImage FitToHeadroom(HdrImage image, float peak, float headroom, float sdrWhiteNits)
    {
        headroom = MathF.Max(1, headroom);
        if (peak <= headroom * 1.02f)
            return image;

        var curve = RenditionBuilder.ToneCurve.Create(HeadroomCurve, peak / headroom, sdrWhiteNits * headroom);
        var result = new HdrImage(image.Width, image.Height);
        float inverse = 1 / headroom;
        ParallelRows.For(image.Height, y =>
        {
            ReadOnlySpan<Half> source = image.ReadRow(y);
            Span<Half> target = result.Row(y);
            for (int i = 0; i < source.Length; i += 4)
            {
                float r = (float)source[i] * inverse, g = (float)source[i + 1] * inverse, b = (float)source[i + 2] * inverse;
                curve.Map(r, g, b, 1, out float mr, out float mg, out float mb);
                target[i] = (Half)(mr * headroom);
                target[i + 1] = (Half)(mg * headroom);
                target[i + 2] = (Half)(mb * headroom);
                target[i + 3] = source[i + 3];
            }
        });
        return result;
    }
}
