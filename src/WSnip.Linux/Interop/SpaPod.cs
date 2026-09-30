using System.Buffers.Binary;

namespace WSnip.Linux.Interop;

/// <summary>Packed pixel formats a screen cast stream can deliver, with SPA's video format ids.</summary>
internal enum SpaVideoFormat : uint
{
    RGBx = 7,
    BGRx = 8,
    xRGB = 9,
    xBGR = 10,
    RGBA = 11,
    BGRA = 12,
    ARGB = 13,
    ABGR = 14,
}

/// <summary>A negotiated raw video format.</summary>
internal readonly record struct SpaVideoInfo(SpaVideoFormat Format, int Width, int Height);

/// <summary>Writes and reads the SPA POD structures that describe stream formats.</summary>
internal static class SpaPod
{
    private const uint TypeId = 3, TypeRectangle = 10, TypeFraction = 11, TypeObject = 15, TypeChoice = 19;
    private const uint ChoiceTypeRange = 1, ChoiceTypeEnum = 3;
    private const uint ObjectFormat = 0x40003;
    public const uint ParamEnumFormat = 3, ParamFormat = 4;
    private const uint KeyMediaType = 1, KeyMediaSubtype = 2, KeyVideoFormat = 0x20001, KeyVideoSize = 0x20003, KeyVideoFramerate = 0x20004;
    private const uint MediaTypeVideo = 2, MediaSubtypeRaw = 1;

    /// <summary>
    /// The formats a capture accepts: raw video in any 8-bit packed RGB layout, at any size and
    /// frame rate. Leaving out DMA-BUF modifiers makes the producer share memory the CPU can map.
    /// </summary>
    public static byte[] CaptureFormats()
    {
        var pod = new Builder();
        int frame = pod.BeginObject(ObjectFormat, ParamEnumFormat);
        pod.Property(KeyMediaType);
        pod.Id(MediaTypeVideo);
        pod.Property(KeyMediaSubtype);
        pod.Id(MediaSubtypeRaw);
        pod.Property(KeyVideoFormat);
        pod.ChoiceEnumId(
            (uint)SpaVideoFormat.BGRx, (uint)SpaVideoFormat.BGRx, (uint)SpaVideoFormat.BGRA, (uint)SpaVideoFormat.RGBx, (uint)SpaVideoFormat.RGBA,
            (uint)SpaVideoFormat.xRGB, (uint)SpaVideoFormat.ARGB, (uint)SpaVideoFormat.xBGR, (uint)SpaVideoFormat.ABGR);
        pod.Property(KeyVideoSize);
        pod.ChoiceRange(TypeRectangle, (1920, 1080), (1, 1), (16384, 16384));
        pod.Property(KeyVideoFramerate);
        pod.ChoiceRange(TypeFraction, (0, 1), (0, 1), (1000, 1));
        pod.End(frame);
        return pod.ToArray();
    }

    /// <summary>Reads a negotiated format, or null when it is not packed 8-bit RGB video.</summary>
    public static SpaVideoInfo? ReadVideoFormat(ReadOnlySpan<byte> pod)
    {
        if (pod.Length < 16 || Read(pod, 4) != TypeObject)
            return null;
        int end = Math.Min(pod.Length, 8 + (int)Read(pod, 0));
        uint mediaType = 0, mediaSubtype = 0, format = 0, width = 0, height = 0;
        for (int offset = 16; offset + 16 <= end;)
        {
            uint key = Read(pod, offset);
            uint size = Read(pod, offset + 8);
            uint type = Read(pod, offset + 12);
            int body = offset + 16;
            if (body + size > end)
                return null;
            ReadOnlySpan<byte> value = pod.Slice(body, (int)size);

            // A choice carries its default as the first of its values.
            if (type == TypeChoice && value.Length >= 16)
            {
                uint childSize = Read(value, 8);
                type = Read(value, 12);
                value = value.Slice(16, (int)Math.Min(childSize, (uint)value.Length - 16));
            }

            switch (key)
            {
                case KeyMediaType when type == TypeId && value.Length >= 4:
                    mediaType = Read(value, 0);
                    break;
                case KeyMediaSubtype when type == TypeId && value.Length >= 4:
                    mediaSubtype = Read(value, 0);
                    break;
                case KeyVideoFormat when type == TypeId && value.Length >= 4:
                    format = Read(value, 0);
                    break;
                case KeyVideoSize when type == TypeRectangle && value.Length >= 8:
                    width = Read(value, 0);
                    height = Read(value, 4);
                    break;
            }

            offset = body + Align(size);
        }

        return mediaType == MediaTypeVideo && mediaSubtype == MediaSubtypeRaw && Enum.IsDefined((SpaVideoFormat)format) &&
               width is > 0 and <= 65536 && height is > 0 and <= 65536
            ? new SpaVideoInfo((SpaVideoFormat)format, (int)width, (int)height)
            : null;
    }

    private static uint Read(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private static int Align(uint size) => (int)((size + 7) & ~7u);

    /// <summary>Builds a POD in native byte order, which is little-endian on the supported architectures.</summary>
    private sealed class Builder
    {
        private byte[] buffer = new byte[256];
        private int length;

        public int BeginObject(uint type, uint id)
        {
            int frame = length;
            Write(0);
            Write(TypeObject);
            Write(type);
            Write(id);
            return frame;
        }

        public void End(int frame) => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(frame), (uint)(length - frame - 8));

        public void Property(uint key)
        {
            Write(key);
            Write(0);
        }

        public void Id(uint value)
        {
            Write(4);
            Write(TypeId);
            Write(value);
            Pad();
        }

        /// <summary>An enumeration of ids, the first being the default.</summary>
        public void ChoiceEnumId(params ReadOnlySpan<uint> values)
        {
            Write(16 + 4 * (uint)values.Length);
            Write(TypeChoice);
            Write(ChoiceTypeEnum);
            Write(0);
            Write(4);
            Write(TypeId);
            foreach (uint value in values)
                Write(value);
            Pad();
        }

        /// <summary>A range of rectangles or fractions: the default, the minimum and the maximum.</summary>
        public void ChoiceRange(uint type, (uint, uint) value, (uint, uint) minimum, (uint, uint) maximum)
        {
            Write(16 + 3 * 8);
            Write(TypeChoice);
            Write(ChoiceTypeRange);
            Write(0);
            Write(8);
            Write(type);
            foreach ((uint first, uint second) in (ReadOnlySpan<(uint, uint)>)[value, minimum, maximum])
            {
                Write(first);
                Write(second);
            }
        }

        public byte[] ToArray() => buffer.AsSpan(0, length).ToArray();

        private void Write(uint value)
        {
            if (length + 4 > buffer.Length)
                Array.Resize(ref buffer, buffer.Length * 2);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(length), value);
            length += 4;
        }

        private void Pad()
        {
            while (length % 8 != 0)
                Write(0);
        }
    }
}
