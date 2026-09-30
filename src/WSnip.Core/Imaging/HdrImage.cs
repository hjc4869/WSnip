using System.Runtime.InteropServices;

namespace WSnip.Core.Imaging;

/// <summary>
/// A linear-light RGBA image with half-float samples on BT.709/sRGB primaries (scRGB).
/// </summary>
/// <remarks>
/// Components may be negative (colors outside the sRGB gamut) or above one (highlights). Whether
/// one unit is diffuse white or 80 nits depends on the producer; see <see cref="Snip"/> and
/// <see cref="Capture.MonitorCapture"/>. Alpha is straight (not premultiplied).
/// </remarks>
public sealed class HdrImage
{
    public const int Channels = 4;

    public HdrImage(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        if ((long)width * height * Channels > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(width), "The image is too large.");

        Width = width;
        Height = height;
        Pixels = GC.AllocateUninitializedArray<Half>(width * height * Channels);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Interleaved R, G, B, A samples, row by row without padding.</summary>
    public Half[] Pixels { get; }

    public int RowLength => Width * Channels;

    public Span<Half> Row(int y) => Pixels.AsSpan(y * RowLength, RowLength);

    public ReadOnlySpan<Half> ReadRow(int y) => Pixels.AsSpan(y * RowLength, RowLength);

    public Span<byte> AsBytes() => MemoryMarshal.AsBytes(Pixels.AsSpan());

    public void Fill(float red, float green, float blue, float alpha)
    {
        Span<Half> first = Row(0);
        for (int x = 0; x < Width; x++)
        {
            first[x * 4] = (Half)red;
            first[x * 4 + 1] = (Half)green;
            first[x * 4 + 2] = (Half)blue;
            first[x * 4 + 3] = (Half)alpha;
        }

        for (int y = 1; y < Height; y++)
            first.CopyTo(Row(y));
    }

    public HdrImage Clone()
    {
        var copy = new HdrImage(Width, Height);
        Pixels.AsSpan().CopyTo(copy.Pixels);
        return copy;
    }

    /// <summary>Copies a rectangle that must lie within the image.</summary>
    public HdrImage Crop(PixelRect rect)
    {
        if (rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0 ||
            rect.Right > Width || rect.Bottom > Height)
            throw new ArgumentOutOfRangeException(nameof(rect));

        var result = new HdrImage(rect.Width, rect.Height);
        for (int y = 0; y < rect.Height; y++)
            Pixels.AsSpan(((rect.Y + y) * Width + rect.X) * Channels, rect.Width * Channels).CopyTo(result.Row(y));
        return result;
    }

    /// <summary>Multiplies the color channels by a constant, leaving alpha untouched.</summary>
    public void ScaleColor(float factor)
    {
        if (factor == 1)
            return;

        ParallelRows.For(Height, y =>
        {
            Span<Half> row = Row(y);
            for (int i = 0; i < row.Length; i += 4)
            {
                row[i] = (Half)((float)row[i] * factor);
                row[i + 1] = (Half)((float)row[i + 1] * factor);
                row[i + 2] = (Half)((float)row[i + 2] * factor);
            }
        });
    }

    public bool HasTransparency()
    {
        for (int y = 0; y < Height; y++)
        {
            ReadOnlySpan<Half> row = ReadRow(y);
            for (int i = 3; i < row.Length; i += 4)
            {
                if ((float)row[i] < 0.999f)
                    return true;
            }
        }

        return false;
    }
}

/// <summary>An integer rectangle in pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    public PixelRect Intersect(PixelRect other)
    {
        int left = Math.Max(X, other.X);
        int top = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? FromEdges(left, top, right, bottom) : default;
    }

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty)
            return other;
        if (other.IsEmpty)
            return this;
        return FromEdges(Math.Min(X, other.X), Math.Min(Y, other.Y),
            Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}

internal static class ParallelRows
{
    /// <summary>Runs a row callback over bands of rows, in parallel for large images.</summary>
    public static void For(int height, Action<int> row)
    {
        int workers = Math.Min(Environment.ProcessorCount, Math.Max(1, height / 32));
        if (workers <= 1)
        {
            for (int y = 0; y < height; y++)
                row(y);
            return;
        }

        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
        {
            int first = (int)((long)height * worker / workers);
            int last = (int)((long)height * (worker + 1) / workers);
            for (int y = first; y < last; y++)
                row(y);
        });
    }
}
