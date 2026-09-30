namespace WSnip.Core.Encoding;

/// <summary>Reads big-endian bit fields and Exp-Golomb codes from a codec header.</summary>
internal ref struct BitReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> data = data;
    private long position;

    public readonly long Position => position;

    public uint Bits(int count)
    {
        uint value = 0;
        for (int i = 0; i < count; i++)
        {
            long bytePosition = position >> 3;
            if (bytePosition >= data.Length)
                throw new InvalidDataException("Unexpected end of a codec header.");
            int bit = (data[(int)bytePosition] >> (7 - (int)(position & 7))) & 1;
            value = (value << 1) | (uint)bit;
            position++;
        }

        return value;
    }

    public bool Flag() => Bits(1) != 0;

    public void Skip(int count)
    {
        for (int remaining = count; remaining > 0; remaining -= Math.Min(remaining, 32))
            Bits(Math.Min(remaining, 32));
    }

    /// <summary>Unsigned Exp-Golomb code, ue(v).</summary>
    public uint UnsignedExpGolomb()
    {
        int zeros = 0;
        while (!Flag())
        {
            if (++zeros > 31)
                throw new InvalidDataException("Invalid Exp-Golomb code.");
        }

        return zeros == 0 ? 0 : (1u << zeros) - 1 + Bits(zeros);
    }

    /// <summary>AV1 uvlc().</summary>
    public uint Uvlc()
    {
        int zeros = 0;
        while (!Flag())
        {
            if (++zeros >= 32)
                return uint.MaxValue;
        }

        return zeros == 0 ? 0 : Bits(zeros) + (1u << zeros) - 1;
    }
}
