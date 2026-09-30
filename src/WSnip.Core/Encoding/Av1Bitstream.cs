namespace WSnip.Core.Encoding;

/// <summary>AV1 OBU handling and the AV1CodecConfigurationRecord of the AV1 ISOBMFF binding.</summary>
internal static class Av1Bitstream
{
    private const int SequenceHeader = 1;
    private const int TemporalDelimiter = 2;
    private const int RedundantFrameHeader = 7;
    private const int Padding = 15;

    public sealed record SequenceInfo(
        int Profile,
        int Level,
        int Tier,
        bool HighBitDepth,
        bool TwelveBit,
        bool Monochrome,
        int SubsamplingX,
        int SubsamplingY,
        int ChromaSamplePosition,
        int Width,
        int Height);

    /// <summary>
    /// Keeps the OBUs an AV1 image item carries: the sequence header and the frame, without
    /// temporal delimiters, padding or redundant frame headers.
    /// </summary>
    public static (byte[] ItemData, byte[] SequenceHeaderObu) Prepare(ReadOnlySpan<byte> packet)
    {
        using var output = new MemoryStream();
        byte[]? sequence = null;
        int position = 0;
        while (position < packet.Length)
        {
            int start = position;
            byte header = packet[position++];
            int type = (header >> 3) & 0xF;
            bool extension = (header & 4) != 0;
            bool hasSize = (header & 2) != 0;
            if (extension)
                position++;
            if (!hasSize)
                throw new InvalidDataException("AV1 OBUs without size fields are not supported.");
            ulong size = ReadLeb128(packet, ref position);
            int end = checked(position + (int)size);
            if (end > packet.Length)
                throw new InvalidDataException("Truncated AV1 OBU.");

            if (type == SequenceHeader && sequence is null)
                sequence = packet[start..end].ToArray();
            if (type is not (TemporalDelimiter or Padding or RedundantFrameHeader))
                output.Write(packet[start..end]);
            position = end;
        }

        return (output.ToArray(), sequence ?? throw new InvalidDataException("The AV1 encoder produced no sequence header."));
    }

    public static SequenceInfo ParseSequenceHeader(byte[] obu)
    {
        int position = 1;
        if ((obu[0] & 4) != 0)
            position++;
        ReadLeb128(obu, ref position);
        var reader = new BitReader(obu.AsSpan(position));

        int profile = (int)reader.Bits(3);
        reader.Flag();
        bool reduced = reader.Flag();
        int level, tier = 0;
        if (reduced)
        {
            level = (int)reader.Bits(5);
        }
        else
        {
            bool timingInfo = reader.Flag();
            bool decoderModelInfo = false;
            int bufferDelayLength = 0;
            if (timingInfo)
            {
                reader.Skip(64);
                if (reader.Flag())
                    reader.Uvlc();
                decoderModelInfo = reader.Flag();
                if (decoderModelInfo)
                {
                    bufferDelayLength = (int)reader.Bits(5) + 1;
                    reader.Skip(32 + 5 + 5);
                }
            }

            bool initialDisplayDelay = reader.Flag();
            int operatingPoints = (int)reader.Bits(5) + 1;
            level = 0;
            for (int i = 0; i < operatingPoints; i++)
            {
                reader.Skip(12);
                int opLevel = (int)reader.Bits(5);
                int opTier = opLevel > 7 ? (int)reader.Bits(1) : 0;
                if (decoderModelInfo && reader.Flag())
                    reader.Skip(bufferDelayLength * 2 + 1);
                if (initialDisplayDelay && reader.Flag())
                    reader.Skip(4);
                if (i == 0)
                {
                    level = opLevel;
                    tier = opTier;
                }
            }
        }

        int widthBits = (int)reader.Bits(4) + 1;
        int heightBits = (int)reader.Bits(4) + 1;
        int width = (int)reader.Bits(widthBits) + 1;
        int height = (int)reader.Bits(heightBits) + 1;
        if (!reduced && reader.Flag())
            reader.Skip(4 + 3);
        reader.Skip(3);
        if (!reduced)
        {
            reader.Skip(4);
            bool orderHint = reader.Flag();
            if (orderHint)
                reader.Skip(2);
            int forceScreenContentTools = reader.Flag() ? 2 : (int)reader.Bits(1);
            if (forceScreenContentTools > 0 && !reader.Flag())
                reader.Skip(1);
            if (orderHint)
                reader.Skip(3);
        }

        reader.Skip(3);

        bool highBitDepth = reader.Flag();
        bool twelveBit = profile == 2 && highBitDepth && reader.Flag();
        int bitDepth = twelveBit ? 12 : highBitDepth ? 10 : 8;
        bool monochrome = profile != 1 && reader.Flag();
        int primaries = 2, transfer = 2, matrix = 2;
        if (reader.Flag())
        {
            primaries = (int)reader.Bits(8);
            transfer = (int)reader.Bits(8);
            matrix = (int)reader.Bits(8);
        }

        int subsamplingX, subsamplingY, chromaPosition = 0;
        if (monochrome)
        {
            reader.Flag();
            subsamplingX = subsamplingY = 1;
        }
        else if (primaries == 1 && transfer == 13 && matrix == 0)
        {
            subsamplingX = subsamplingY = 0;
        }
        else
        {
            reader.Flag();
            if (profile == 0)
            {
                subsamplingX = subsamplingY = 1;
            }
            else if (profile == 1)
            {
                subsamplingX = subsamplingY = 0;
            }
            else if (bitDepth == 12)
            {
                subsamplingX = (int)reader.Bits(1);
                subsamplingY = subsamplingX == 1 ? (int)reader.Bits(1) : 0;
            }
            else
            {
                subsamplingX = 1;
                subsamplingY = 0;
            }

            if (subsamplingX == 1 && subsamplingY == 1)
                chromaPosition = (int)reader.Bits(2);
        }

        return new SequenceInfo(profile, level, tier, highBitDepth, twelveBit, monochrome,
            subsamplingX, subsamplingY, chromaPosition, width, height);
    }

    /// <summary>The four fixed bytes of av1C; configOBUs are omitted as AVIF permits.</summary>
    public static byte[] ConfigurationRecord(SequenceInfo info) =>
    [
        0x81,
        (byte)((info.Profile << 5) | (info.Level & 0x1F)),
        (byte)((info.Tier << 7) | (info.HighBitDepth ? 0x40 : 0) | (info.TwelveBit ? 0x20 : 0) |
            (info.Monochrome ? 0x10 : 0) | (info.SubsamplingX << 3) | (info.SubsamplingY << 2) | info.ChromaSamplePosition),
        0,
    ];

    private static ulong ReadLeb128(ReadOnlySpan<byte> data, ref int position)
    {
        ulong value = 0;
        for (int i = 0; i < 8; i++)
        {
            if (position >= data.Length)
                throw new InvalidDataException("Truncated AV1 size field.");
            byte b = data[position++];
            value |= (ulong)(b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0)
                break;
        }

        return value;
    }
}
