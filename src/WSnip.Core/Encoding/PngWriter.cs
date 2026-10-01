using System.Buffers.Binary;
using System.IO.Compression;
using SkiaSharp;
using WSnip.Core.Imaging;

using WSnip.Core.Strings;

namespace WSnip.Core.Encoding;

/// <summary>Writes 8-bit SDR PNG through Skia and 16-bit HDR PNG tagged with a cICP chunk.</summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void WriteSdr(Stream output, Rendition rendition)
    {
        SkiaImages.WithSdrPixmap(rendition, pixmap =>
        {
            if (!pixmap.Encode(output, new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6)))
                throw new InvalidOperationException(AppStrings.PngEncodingFailed);
            return true;
        });
    }

    /// <summary>
    /// Writes BT.2100 PQ samples. The cICP chunk (PNG third edition) identifies the primaries and
    /// transfer function, and cLLI carries the content light levels.
    /// </summary>
    public static void WriteHdr(Stream output, PqImage image, CompressionLevel level = CompressionLevel.Optimal)
    {
        int channels = image.HasAlpha ? 4 : 3;
        int rowBytes = image.Width * channels * 2;
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)image.Height);
        header[8] = 16;
        header[9] = (byte)(image.HasAlpha ? 6 : 2);
        header[10..].Clear();
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "cICP"u8, [(byte)ColorMath.CicpPrimaries(image.Primaries), ColorMath.CicpTransferPq, 0, 1]);

        Span<byte> light = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(light, (uint)Math.Round(image.MaxContentLightLevel * 10000));
        BinaryPrimitives.WriteUInt32BigEndian(light[4..], (uint)Math.Round(image.MaxFrameAverageLightLevel * 10000));
        WriteChunk(output, "cLLI"u8, light);

        using (var idat = new ChunkStream(output, "IDAT"u8.ToArray()))
        using (var zlib = new ZLibStream(idat, level, leaveOpen: true))
            WriteFilteredRows(zlib, image, channels, rowBytes);

        WriteChunk(output, "IEND"u8, []);
    }

    private static void WriteFilteredRows(Stream output, PqImage image, int channels, int rowBytes)
    {
        // Only four rows of scratch space, independent of image height. Previously a full
        // filtered image and three new arrays per row survived until compression completed.
        using var scratch = new PixelBuffer<byte>(checked(rowBytes * 4 + 1));
        Span<byte> current = scratch.AsSpan(0, rowBytes);
        Span<byte> previous = scratch.AsSpan(rowBytes, rowBytes);
        Span<byte> candidate = scratch.AsSpan(rowBytes * 2, rowBytes);
        Span<byte> bestRow = scratch.AsSpan(rowBytes * 3, rowBytes + 1);
        previous.Clear();
        for (int y = 0; y < image.Height; y++)
        {
            Pack(image, y, channels, current);
            long best = long.MaxValue;
            int bpp = channels * 2;
            for (byte filter = 0; filter <= 4; filter++)
            {
                long cost = 0;
                for (int i = 0; i < rowBytes; i++)
                {
                    int left = i >= bpp ? current[i - bpp] : 0;
                    int up = previous[i];
                    int upLeft = i >= bpp ? previous[i - bpp] : 0;
                    int value = filter switch
                    {
                        1 => current[i] - left,
                        2 => current[i] - up,
                        3 => current[i] - ((left + up) >> 1),
                        4 => current[i] - Paeth(left, up, upLeft),
                        _ => current[i],
                    };
                    candidate[i] = (byte)value;
                    cost += Math.Abs((int)(sbyte)(byte)value);
                }

                if (cost < best)
                {
                    best = cost;
                    bestRow[0] = filter;
                    candidate.CopyTo(bestRow[1..]);
                }
            }
            output.Write(bestRow);
            Span<byte> swap = previous;
            previous = current;
            current = swap;
        }
    }

    private static void Pack(PqImage image, int y, int channels, Span<byte> row)
    {
        ReadOnlySpan<ushort> source = image.Pixels.AsSpan(y * image.Width * 4, image.Width * 4);
        int target = 0;
        for (int x = 0; x < image.Width; x++)
        {
            for (int c = 0; c < channels; c++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(row[target..], source[x * 4 + c]);
                target += 2;
            }
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    internal static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(length, crc);
        output.Write(length);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    /// <summary>Buffers written data into PNG chunks of a fixed type.</summary>
    private sealed class ChunkStream(Stream output, byte[] chunkType) : Stream
    {
        private const int ChunkSize = 1 << 16;
        private readonly MemoryStream buffer = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] data, int offset, int count) => Write(data.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            buffer.Write(data);
            if (buffer.Length >= ChunkSize)
                Emit();
        }

        public override void Flush()
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && buffer.Length > 0)
                Emit();
            if (disposing)
                buffer.Dispose();
            base.Dispose(disposing);
        }

        private void Emit()
        {
            WriteChunk(output, chunkType, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            buffer.SetLength(0);
        }

        public override int Read(byte[] data, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
