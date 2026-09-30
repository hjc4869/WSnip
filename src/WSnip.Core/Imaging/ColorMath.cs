namespace WSnip.Core.Imaging;

/// <summary>RGB primaries used by encoded outputs, all with a D65 white point.</summary>
public enum ColorPrimaries
{
    Bt709,
    DisplayP3,
    Bt2020,
}

/// <summary>Primaries, transfer functions and CICP code points shared by the image pipeline.</summary>
public static class ColorMath
{
    public const double PqMaximumNits = 10000;

    /// <summary>Diffuse white of relative HDR content, following ITU-R BT.2408.</summary>
    public const double ReferenceWhiteNits = 203;

    /// <summary>The luminance that one scRGB unit represents on an HDR composition surface.</summary>
    public const double ScRgbUnitNits = 80;

    private static readonly double[][] Primaries =
    [
        [0.640, 0.330, 0.300, 0.600, 0.150, 0.060],
        [0.680, 0.320, 0.265, 0.690, 0.150, 0.060],
        [0.708, 0.292, 0.170, 0.797, 0.131, 0.046],
    ];

    private static readonly double[][] ToXyz = Primaries.Select(p => RgbToXyz(p)).ToArray();
    private static readonly double[][] FromXyz = ToXyz.Select(Invert).ToArray();

    /// <summary>Row-major 3x3 matrix converting linear RGB between two sets of primaries.</summary>
    public static float[] Matrix(ColorPrimaries from, ColorPrimaries to)
    {
        double[] m = Multiply(FromXyz[(int)to], ToXyz[(int)from]);
        return m.Select(v => (float)v).ToArray();
    }

    /// <summary>Luminance weights (the Y row of the RGB-to-XYZ matrix).</summary>
    public static (float R, float G, float B) Luminance(ColorPrimaries primaries)
    {
        double[] m = ToXyz[(int)primaries];
        return ((float)m[3], (float)m[4], (float)m[5]);
    }

    /// <summary>ITU-T H.273 colour_primaries code.</summary>
    public static int CicpPrimaries(ColorPrimaries primaries) => primaries switch
    {
        ColorPrimaries.DisplayP3 => 12,
        ColorPrimaries.Bt2020 => 9,
        _ => 1,
    };

    public const int CicpTransferSrgb = 13;
    public const int CicpTransferLinear = 8;
    public const int CicpTransferPq = 16;
    public const int CicpMatrixIdentity = 0;
    public const int CicpMatrixBt709 = 1;
    public const int CicpMatrixUnspecified = 2;
    public const int CicpMatrixBt601 = 6;

    public static float SrgbToLinear(float encoded)
    {
        float magnitude = Math.Abs(encoded);
        float linear = magnitude <= 0.04045f
            ? magnitude / 12.92f
            : MathF.Pow((magnitude + 0.055f) / 1.055f, 2.4f);
        return MathF.CopySign(linear, encoded);
    }

    public static float LinearToSrgb(float linear)
    {
        float magnitude = Math.Abs(linear);
        float encoded = magnitude <= 0.0031308f
            ? magnitude * 12.92f
            : 1.055f * MathF.Pow(magnitude, 1 / 2.4f) - 0.055f;
        return MathF.CopySign(encoded, linear);
    }

    /// <summary>SMPTE ST 2084 inverse EOTF for an absolute luminance in nits.</summary>
    public static float NitsToPq(float nits)
    {
        const float m1 = 2610f / 16384f;
        const float m2 = 2523f / 4096f * 128f;
        const float c1 = 3424f / 4096f;
        const float c2 = 2413f / 4096f * 32f;
        const float c3 = 2392f / 4096f * 32f;
        float y = Math.Clamp(nits / (float)PqMaximumNits, 0f, 1f);
        float p = MathF.Pow(y, m1);
        return MathF.Pow((c1 + c2 * p) / (1f + c3 * p), m2);
    }

    public static float PqToNits(float encoded)
    {
        const float m1 = 2610f / 16384f;
        const float m2 = 2523f / 4096f * 128f;
        const float c1 = 3424f / 4096f;
        const float c2 = 2413f / 4096f * 32f;
        const float c3 = 2392f / 4096f * 32f;
        float e = MathF.Pow(Math.Clamp(encoded, 0f, 1f), 1f / m2);
        float y = MathF.Pow(Math.Max(e - c1, 0f) / (c2 - c3 * e), 1f / m1);
        return y * (float)PqMaximumNits;
    }

    /// <summary>Linear value in [0, 1] to an 8-bit sRGB code, through a lookup table.</summary>
    public static byte LinearToSrgbByte(float linear)
    {
        if (!(linear > 0))
            return 0;
        if (linear >= 1)
            return 255;
        return SrgbEncodeTable[(int)(linear * (SrgbEncodeTable.Length - 1) + 0.5f)];
    }

    public static float SrgbByteToLinear(byte code) => SrgbDecodeTable[code];

    private static readonly byte[] SrgbEncodeTable = BuildEncodeTable();
    private static readonly float[] SrgbDecodeTable = Enumerable.Range(0, 256)
        .Select(code => SrgbToLinear(code / 255f)).ToArray();

    private static byte[] BuildEncodeTable()
    {
        var table = new byte[65536];
        for (int i = 0; i < table.Length; i++)
            table[i] = (byte)Math.Clamp((int)MathF.Round(LinearToSrgb(i / 65535f) * 255f), 0, 255);
        return table;
    }

    private static double[] RgbToXyz(double[] p)
    {
        const double wx = 0.3127, wy = 0.3290;
        double[] columns =
        [
            p[0] / p[1], p[2] / p[3], p[4] / p[5],
            1, 1, 1,
            (1 - p[0] - p[1]) / p[1], (1 - p[2] - p[3]) / p[3], (1 - p[4] - p[5]) / p[5],
        ];
        double[] inverse = Invert(columns);
        double[] white = [wx / wy, 1, (1 - wx - wy) / wy];
        double[] scale = new double[3];
        for (int r = 0; r < 3; r++)
            scale[r] = inverse[r * 3] * white[0] + inverse[r * 3 + 1] * white[1] + inverse[r * 3 + 2] * white[2];

        var result = new double[9];
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
            result[r * 3 + c] = columns[r * 3 + c] * scale[c];
        return result;
    }

    private static double[] Multiply(double[] a, double[] b)
    {
        var result = new double[9];
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
            result[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
        return result;
    }

    private static double[] Invert(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        return
        [
            (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det,
            (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det,
            (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det,
        ];
    }
}
