namespace WSnip.Core.Encoding;

/// <summary>HEVC Annex B parsing and the HEVCDecoderConfigurationRecord of ISO/IEC 14496-15.</summary>
internal static class HevcBitstream
{
    public const int VpsType = 32;
    public const int SpsType = 33;
    public const int PpsType = 34;
    public const int AudType = 35;
    public const int PrefixSeiType = 39;

    public static int NalType(byte[] nal) => (nal[0] >> 1) & 0x3F;

    /// <summary>Splits a start-code delimited stream into NAL units without their start codes.</summary>
    public static List<byte[]> SplitAnnexB(ReadOnlySpan<byte> stream)
    {
        var units = new List<byte[]>();
        int start = -1;
        int i = 0;
        while (i + 3 <= stream.Length)
        {
            int codeLength = stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1 ? 3
                : i + 4 <= stream.Length && stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 0 && stream[i + 3] == 1 ? 4
                : 0;
            if (codeLength == 0)
            {
                i++;
                continue;
            }

            if (start >= 0)
                units.Add(TrimTrailingZeros(stream[start..i]));
            i += codeLength;
            start = i;
        }

        if (start >= 0 && start < stream.Length)
            units.Add(TrimTrailingZeros(stream[start..]));
        return units.Where(unit => unit.Length >= 2).ToList();
    }

    public static byte[] LengthPrefixed(IEnumerable<byte[]> units)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (byte[] unit in units)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)unit.Length);
            stream.Write(length);
            stream.Write(unit);
        }

        return stream.ToArray();
    }

    public static byte[] Rbsp(ReadOnlySpan<byte> payload)
    {
        var result = new List<byte>(payload.Length);
        int zeros = 0;
        foreach (byte value in payload)
        {
            if (zeros >= 2 && value == 3)
            {
                zeros = 0;
                continue;
            }

            result.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }

        return result.ToArray();
    }

    public sealed record SequenceInfo(
        byte[] GeneralProfileTierLevel,
        int MaxSubLayers,
        bool TemporalIdNesting,
        int ChromaFormat,
        int BitDepthLuma,
        int BitDepthChroma,
        int Width,
        int Height);

    public static SequenceInfo ParseSps(byte[] nal)
    {
        byte[] rbsp = Rbsp(nal.AsSpan(2));
        var reader = new BitReader(rbsp);
        reader.Bits(4);
        int maxSubLayersMinus1 = (int)reader.Bits(3);
        bool nesting = reader.Flag();
        byte[] ptl = rbsp.AsSpan(1, 12).ToArray();
        reader.Skip(12 * 8);

        var profilePresent = new bool[8];
        var levelPresent = new bool[8];
        for (int i = 0; i < maxSubLayersMinus1; i++)
        {
            profilePresent[i] = reader.Flag();
            levelPresent[i] = reader.Flag();
        }

        if (maxSubLayersMinus1 > 0)
            reader.Skip((8 - maxSubLayersMinus1) * 2);
        for (int i = 0; i < maxSubLayersMinus1; i++)
        {
            if (profilePresent[i])
                reader.Skip(88);
            if (levelPresent[i])
                reader.Skip(8);
        }

        reader.UnsignedExpGolomb();
        int chromaFormat = (int)reader.UnsignedExpGolomb();
        if (chromaFormat == 3)
            reader.Flag();
        int width = (int)reader.UnsignedExpGolomb();
        int height = (int)reader.UnsignedExpGolomb();
        if (reader.Flag())
        {
            int subWidth = chromaFormat is 1 or 2 ? 2 : 1;
            int subHeight = chromaFormat == 1 ? 2 : 1;
            int left = (int)reader.UnsignedExpGolomb();
            int right = (int)reader.UnsignedExpGolomb();
            int top = (int)reader.UnsignedExpGolomb();
            int bottom = (int)reader.UnsignedExpGolomb();
            width -= subWidth * (left + right);
            height -= subHeight * (top + bottom);
        }

        int bitDepthLuma = (int)reader.UnsignedExpGolomb() + 8;
        int bitDepthChroma = (int)reader.UnsignedExpGolomb() + 8;
        return new SequenceInfo(ptl, maxSubLayersMinus1 + 1, nesting, chromaFormat, bitDepthLuma, bitDepthChroma, width, height);
    }

    /// <summary>Builds the hvcC payload holding the parameter sets.</summary>
    public static byte[] ConfigurationRecord(SequenceInfo info, IReadOnlyList<byte[]> vps, IReadOnlyList<byte[]> sps, IReadOnlyList<byte[]> pps)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(1);
        stream.Write(info.GeneralProfileTierLevel);
        stream.WriteByte(0xF0);
        stream.WriteByte(0x00);
        stream.WriteByte(0xFC);
        stream.WriteByte((byte)(0xFC | info.ChromaFormat));
        stream.WriteByte((byte)(0xF8 | (info.BitDepthLuma - 8)));
        stream.WriteByte((byte)(0xF8 | (info.BitDepthChroma - 8)));
        stream.WriteByte(0);
        stream.WriteByte(0);
        stream.WriteByte((byte)((Math.Min(info.MaxSubLayers, 7) << 3) | (info.TemporalIdNesting ? 4 : 0) | 3));

        var arrays = new (int Type, IReadOnlyList<byte[]> Units)[] { (VpsType, vps), (SpsType, sps), (PpsType, pps) };
        stream.WriteByte((byte)arrays.Count(array => array.Units.Count > 0));
        foreach ((int type, IReadOnlyList<byte[]> units) in arrays)
        {
            if (units.Count == 0)
                continue;
            stream.WriteByte((byte)(0x80 | type));
            WriteUInt16(stream, units.Count);
            foreach (byte[] unit in units)
            {
                WriteUInt16(stream, unit.Length);
                stream.Write(unit);
            }
        }

        return stream.ToArray();
    }

    private static void WriteUInt16(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static byte[] TrimTrailingZeros(ReadOnlySpan<byte> unit)
    {
        int end = unit.Length;
        while (end > 0 && unit[end - 1] == 0)
            end--;
        return unit[..end].ToArray();
    }
}
