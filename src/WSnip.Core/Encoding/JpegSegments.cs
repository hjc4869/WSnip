using System.Buffers.Binary;

namespace WSnip.Core.Encoding;

/// <summary>A JPEG split into its header marker segments and the scan data that follows them.</summary>
internal sealed class JpegSegments
{
    private JpegSegments(List<(byte Marker, byte[] Segment)> header, byte[] scan)
    {
        Header = header;
        Scan = scan;
    }

    /// <summary>Complete marker segments (marker bytes included) between SOI and the first SOS.</summary>
    public List<(byte Marker, byte[] Segment)> Header { get; }

    /// <summary>Everything from the first SOS marker to the end of the image.</summary>
    public byte[] Scan { get; }

    public static JpegSegments Parse(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new InvalidDataException("The encoder did not produce a JPEG stream.");

        var header = new List<(byte, byte[])>();
        int position = 2;
        while (position + 4 <= jpeg.Length)
        {
            if (jpeg[position] != 0xFF)
                throw new InvalidDataException("Malformed JPEG marker sequence.");
            byte marker = jpeg[position + 1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            if (marker == 0xDA)
                return new JpegSegments(header, jpeg[position..].ToArray());

            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(position + 2)..]);
            if (length < 2 || position + 2 + length > jpeg.Length)
                throw new InvalidDataException("Truncated JPEG marker segment.");
            header.Add((marker, jpeg.Slice(position, 2 + length).ToArray()));
            position += 2 + length;
        }

        throw new InvalidDataException("The JPEG stream has no scan.");
    }

    public static byte[] Segment(byte marker, ReadOnlySpan<byte> identifier, ReadOnlySpan<byte> payload)
    {
        int length = 2 + identifier.Length + payload.Length;
        if (length > ushort.MaxValue)
            throw new InvalidDataException("A JPEG marker segment cannot exceed 64 KiB.");

        var segment = new byte[2 + length];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)length);
        identifier.CopyTo(segment.AsSpan(4));
        payload.CopyTo(segment.AsSpan(4 + identifier.Length));
        return segment;
    }

    public static bool IsApp(byte marker) => marker is >= 0xE0 and <= 0xEF;

    public static bool StartsWith(byte[] segment, ReadOnlySpan<byte> identifier) =>
        segment.Length >= 4 + identifier.Length && segment.AsSpan(4, identifier.Length).SequenceEqual(identifier);
}
