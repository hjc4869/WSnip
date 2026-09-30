namespace WSnip.Core.Imaging;

/// <summary>
/// A color in linear light on BT.709 primaries, relative to SDR white (scRGB as the snips hold it).
/// Components below zero are colors outside sRGB and components above one are brighter than SDR
/// white.
/// </summary>
public readonly record struct ScRgb(float R, float G, float B)
{
    private const float Tolerance = 1f / 512;

    public static ScRgb White => new(1, 1, 1);

    /// <summary>Relative luminance, where 1 is SDR white.</summary>
    public float Luminance
    {
        get
        {
            (float r, float g, float b) = ColorMath.Luminance(ColorPrimaries.Bt709);
            return r * R + g * G + b * B;
        }
    }

    public float Peak => MathF.Max(R, MathF.Max(G, B));

    /// <summary>
    /// Whether the color is brighter than SDR white allows. Wide-gamut colors exceed one on BT.709
    /// primaries without being HDR, so this looks at BT.2020, which holds every SDR color within 0 to 1.
    /// </summary>
    public bool IsHdr
    {
        get
        {
            (float r, float g, float b) = ToLinear(ColorPrimaries.Bt2020);
            return MathF.Max(r, MathF.Max(g, b)) > 1 + Tolerance;
        }
    }

    /// <summary>The narrowest of sRGB, Display P3 and BT.2020 that contains the color.</summary>
    public ColorPrimaries Gamut
    {
        get
        {
            if (Contains(ColorPrimaries.Bt709))
                return ColorPrimaries.Bt709;
            return Contains(ColorPrimaries.DisplayP3) ? ColorPrimaries.DisplayP3 : ColorPrimaries.Bt2020;
        }
    }

    public static ScRgb FromSrgb(byte red, byte green, byte blue) =>
        new(ColorMath.SrgbByteToLinear(red), ColorMath.SrgbByteToLinear(green), ColorMath.SrgbByteToLinear(blue));

    /// <summary>Converts linear components on other primaries to BT.709.</summary>
    public static ScRgb FromLinear(ColorPrimaries primaries, float red, float green, float blue)
    {
        if (primaries == ColorPrimaries.Bt709)
            return new ScRgb(red, green, blue);
        float[] m = ColorMath.Matrix(primaries, ColorPrimaries.Bt709);
        return new ScRgb(
            m[0] * red + m[1] * green + m[2] * blue,
            m[3] * red + m[4] * green + m[5] * blue,
            m[6] * red + m[7] * green + m[8] * blue);
    }

    /// <summary>Linear components on other primaries.</summary>
    public (float R, float G, float B) ToLinear(ColorPrimaries primaries)
    {
        if (primaries == ColorPrimaries.Bt709)
            return (R, G, B);
        float[] m = ColorMath.Matrix(ColorPrimaries.Bt709, primaries);
        return (m[0] * R + m[1] * G + m[2] * B, m[3] * R + m[4] * G + m[5] * B, m[6] * R + m[7] * G + m[8] * B);
    }

    public bool Contains(ColorPrimaries primaries)
    {
        (float r, float g, float b) = ToLinear(primaries);
        float limit = -Tolerance * MathF.Max(1, Peak);
        return r >= limit && g >= limit && b >= limit;
    }

    /// <summary>The nearest SDR sRGB color, clipping each component.</summary>
    public ScRgb ClampToSdr() => new(Math.Clamp(R, 0, 1), Math.Clamp(G, 0, 1), Math.Clamp(B, 0, 1));

    public ScRgb Scale(float factor) => new(R * factor, G * factor, B * factor);

    /// <summary>8-bit sRGB of the clipped color, for UI that cannot show more.</summary>
    public (byte R, byte G, byte B) ToSrgbBytes() =>
        (ColorMath.LinearToSrgbByte(R), ColorMath.LinearToSrgbByte(G), ColorMath.LinearToSrgbByte(B));
}

/// <summary>
/// A user-defined annotation color as it was entered: components on a chosen set of primaries,
/// encoded with the sRGB transfer function as color pickers show them, and a brightness that
/// scales the linear light so the color can be brighter than SDR white.
/// </summary>
/// <param name="Primaries">sRGB, Display P3 or BT.2020.</param>
/// <param name="Red">Encoded red, 0 to 1.</param>
/// <param name="Green">Encoded green, 0 to 1.</param>
/// <param name="Blue">Encoded blue, 0 to 1.</param>
/// <param name="Brightness">Linear multiplier; 1 keeps the color within SDR.</param>
public sealed record CustomColor(ColorPrimaries Primaries, float Red, float Green, float Blue, float Brightness = 1)
{
    public const float MaxBrightness = 16;

    public ScRgb ToScRgb()
    {
        float scale = Math.Clamp(Brightness, 0, MaxBrightness);
        return ScRgb.FromLinear(Primaries,
            ColorMath.SrgbToLinear(Math.Clamp(Red, 0, 1)) * scale,
            ColorMath.SrgbToLinear(Math.Clamp(Green, 0, 1)) * scale,
            ColorMath.SrgbToLinear(Math.Clamp(Blue, 0, 1)) * scale);
    }

    /// <summary>
    /// Describes a measured color on the narrowest primaries that hold it, moving anything above
    /// SDR white into the brightness so the components stay between 0 and 1.
    /// </summary>
    public static CustomColor FromScRgb(ScRgb color)
    {
        ColorPrimaries primaries = color.Gamut;
        (float r, float g, float b) = color.ToLinear(primaries);
        r = MathF.Max(0, r);
        g = MathF.Max(0, g);
        b = MathF.Max(0, b);
        float peak = MathF.Max(r, MathF.Max(g, b));
        float brightness = peak > 1 ? MathF.Min(peak, MaxBrightness) : 1;
        return new CustomColor(primaries,
            MathF.Min(1, ColorMath.LinearToSrgb(r / brightness)),
            MathF.Min(1, ColorMath.LinearToSrgb(g / brightness)),
            MathF.Min(1, ColorMath.LinearToSrgb(b / brightness)),
            brightness);
    }
}
