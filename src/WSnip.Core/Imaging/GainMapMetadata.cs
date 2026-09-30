using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace WSnip.Core.Imaging;

/// <summary>ISO 21496-1 gain map parameters, in log2 units where the standard uses them.</summary>
/// <remarks>
/// The base rendition is SDR (headroom 1, log2 0) and the alternate is HDR. Offsets and gamma
/// are per channel; single-channel metadata repeats one value.
/// </remarks>
public sealed record GainMapMetadata(
    float[] GainMapMin,
    float[] GainMapMax,
    float[] Gamma,
    float[] BaseOffset,
    float[] AlternateOffset,
    float BaseHdrHeadroom,
    float AlternateHdrHeadroom,
    bool UseBaseColorSpace = true)
{
    public const string IsoNamespace = "urn:iso:std:iso:ts:21496:-1";

    public bool IsMultichannel =>
        !AllEqual(GainMapMin) || !AllEqual(GainMapMax) || !AllEqual(Gamma) ||
        !AllEqual(BaseOffset) || !AllEqual(AlternateOffset);

    /// <summary>
    /// The GainMapMetadata syntax of ISO 21496-1 clause C.2.2, starting at minimum_version. Every
    /// fraction carries its own denominator, the form all current readers accept.
    /// </summary>
    public byte[] ToIsoBinary()
    {
        int channels = IsMultichannel ? 3 : 1;
        var buffer = new byte[2 + 2 + 1 + 16 + channels * 40];
        Span<byte> span = buffer;
        BinaryPrimitives.WriteUInt16BigEndian(span, 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], 0);
        byte flags = 0;
        if (channels == 3)
            flags |= 0x80;
        if (UseBaseColorSpace)
            flags |= 0x40;
        span[4] = flags;
        int offset = 5;
        WriteUnsigned(span, ref offset, BaseHdrHeadroom);
        WriteUnsigned(span, ref offset, AlternateHdrHeadroom);
        for (int c = 0; c < channels; c++)
        {
            WriteSigned(span, ref offset, GainMapMin[c]);
            WriteSigned(span, ref offset, GainMapMax[c]);
            WriteUnsigned(span, ref offset, Gamma[c]);
            WriteSigned(span, ref offset, BaseOffset[c]);
            WriteSigned(span, ref offset, AlternateOffset[c]);
        }

        return buffer;
    }

    /// <summary>The Adobe <c>hdrgm</c> description of a gain map image.</summary>
    public string ToGainMapXmp()
    {
        var builder = new StringBuilder();
        builder.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"WSnip\">");
        builder.Append("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">");
        builder.Append("<rdf:Description rdf:about=\"\" xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\"");
        builder.Append(" hdrgm:Version=\"1.0\"");
        builder.Append(" hdrgm:BaseRenditionIsHDR=\"False\"");
        builder.Append(CultureInfo.InvariantCulture, $" hdrgm:HDRCapacityMin=\"{Format(BaseHdrHeadroom)}\"");
        builder.Append(CultureInfo.InvariantCulture, $" hdrgm:HDRCapacityMax=\"{Format(AlternateHdrHeadroom)}\"");
        var sequences = new StringBuilder();
        AppendValue(builder, sequences, "GainMapMin", GainMapMin);
        AppendValue(builder, sequences, "GainMapMax", GainMapMax);
        AppendValue(builder, sequences, "Gamma", Gamma);
        AppendValue(builder, sequences, "OffsetSDR", BaseOffset);
        AppendValue(builder, sequences, "OffsetHDR", AlternateOffset);
        if (sequences.Length == 0)
        {
            builder.Append("/>");
        }
        else
        {
            builder.Append('>');
            builder.Append(sequences);
            builder.Append("</rdf:Description>");
        }

        builder.Append("</rdf:RDF></x:xmpmeta>");
        return builder.ToString();
    }

    /// <summary>
    /// Headroom-dependent weight of the gain map for a display, following ISO 21496-1: zero at or
    /// below the base headroom and one at or above the alternate headroom.
    /// </summary>
    public float Weight(double displayHeadroom)
    {
        if (!(displayHeadroom > 1) || AlternateHdrHeadroom <= BaseHdrHeadroom)
            return 0;
        double log = Math.Log2(displayHeadroom);
        return (float)Math.Clamp((log - BaseHdrHeadroom) / (AlternateHdrHeadroom - BaseHdrHeadroom), 0, 1);
    }

    private static void AppendValue(StringBuilder attributes, StringBuilder sequences, string name, float[] values)
    {
        if (AllEqual(values))
        {
            attributes.Append(CultureInfo.InvariantCulture, $" hdrgm:{name}=\"{Format(values[0])}\"");
            return;
        }

        sequences.Append(CultureInfo.InvariantCulture, $"<hdrgm:{name}><rdf:Seq>");
        foreach (float value in values)
            sequences.Append(CultureInfo.InvariantCulture, $"<rdf:li>{Format(value)}</rdf:li>");
        sequences.Append(CultureInfo.InvariantCulture, $"</rdf:Seq></hdrgm:{name}>");
    }

    private static string Format(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static bool AllEqual(float[] values) => values[0] == values[1] && values[0] == values[2];

    private static void WriteSigned(Span<byte> span, ref int offset, float value)
    {
        (long numerator, long denominator) = Fraction(value, signed: true);
        BinaryPrimitives.WriteInt32BigEndian(span[offset..], (int)numerator);
        BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 4)..], (uint)denominator);
        offset += 8;
    }

    private static void WriteUnsigned(Span<byte> span, ref int offset, float value)
    {
        (long numerator, long denominator) = Fraction(Math.Max(value, 0), signed: false);
        BinaryPrimitives.WriteUInt32BigEndian(span[offset..], (uint)numerator);
        BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 4)..], (uint)denominator);
        offset += 8;
    }

    /// <summary>Best rational approximation whose terms fit the 32-bit fields.</summary>
    internal static (long Numerator, long Denominator) Fraction(double value, bool signed)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value));

        long limit = signed ? int.MaxValue : uint.MaxValue;
        double magnitude = Math.Abs(value);
        long h0 = 0, h1 = 1, k0 = 1, k1 = 0;
        double x = magnitude;
        for (int i = 0; i < 64; i++)
        {
            double floor = Math.Floor(x);
            if (floor > limit)
                break;
            long a = (long)floor;
            long h2 = a * h1 + h0;
            long k2 = a * k1 + k0;
            if (h2 > limit || k2 > uint.MaxValue)
                break;
            h0 = h1; h1 = h2;
            k0 = k1; k1 = k2;
            double fraction = x - floor;
            if (fraction < 1e-12 || Math.Abs((double)h1 / k1 - magnitude) < 1e-9 * Math.Max(1, magnitude))
                break;
            x = 1 / fraction;
        }

        if (k1 == 0)
            return (value < 0 ? -limit : limit, 1);
        return (value < 0 ? -h1 : h1, k1);
    }
}
