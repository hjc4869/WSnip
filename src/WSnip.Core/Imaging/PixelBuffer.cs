using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WSnip.Core.Imaging;

/// <summary>
/// An explicitly owned, tightly packed native pixel buffer. Each <see cref="Share"/> owns a
/// separate lease; the allocation is freed as soon as the last lease is disposed, not at the next GC.
/// </summary>
/// <remarks>
/// A span or pointer is borrowed for the lifetime of this lease. Give asynchronous work and native
/// consumers their own lease, and do not modify pixels after publishing them to a renderer.
/// </remarks>
public sealed unsafe class PixelBuffer<T> : IDisposable where T : unmanaged
{
    private static long allocatedBytes;
    private Allocation? allocation;

    /// <summary>Live native storage, excluding additional leases on the same allocation.</summary>
    internal static long AllocatedBytes => Interlocked.Read(ref allocatedBytes);

    public PixelBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        // Byte spans, Skia and the encoders also need to address the entire buffer with an int.
        if ((long)length * sizeof(T) > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(length), "The pixel buffer is too large.");
        Length = length;
        allocation = new Allocation(checked(length * sizeof(T)));
    }

    private PixelBuffer(Allocation allocation, int length)
    {
        allocation.AddRef();
        this.allocation = allocation;
        Length = length;
    }

    ~PixelBuffer() => Dispose();

    public int Length { get; }

    public int ByteLength => checked(Length * sizeof(T));

    public bool IsDisposed => allocation is null;

    public nint Pointer => (allocation ?? throw new ObjectDisposedException(nameof(PixelBuffer<T>))).DangerousGetHandle();

    public Span<T> Span => new((void*)Pointer, Length);

    public ref T this[int index] => ref Span[index];

    public Span<T> AsSpan() => Span;

    public Span<T> AsSpan(int start, int length) => Span.Slice(start, length);

    public Span<byte> AsBytes() => MemoryMarshal.AsBytes(Span);

    public ref T GetPinnableReference() => ref MemoryMarshal.GetReference(Span);

    /// <summary>Retains the allocation without copying any pixels.</summary>
    public PixelBuffer<T> Share() => new(allocation ?? throw new ObjectDisposedException(nameof(PixelBuffer<T>)), Length);

    public void Dispose()
    {
        Interlocked.Exchange(ref allocation, null)?.Release();
        GC.SuppressFinalize(this);
    }

    private sealed class Allocation : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly int bytes;
        private int references = 1;

        public Allocation(int bytes) : base(ownsHandle: true)
        {
            this.bytes = bytes;
            void* memory = NativeMemory.Alloc((nuint)bytes);
            if (memory is null)
                throw new OutOfMemoryException();
            SetHandle((nint)memory);
            GC.AddMemoryPressure(bytes);
            Interlocked.Add(ref allocatedBytes, bytes);
        }

        public void AddRef()
        {
            int count = Volatile.Read(ref references);
            while (true)
            {
                ObjectDisposedException.ThrowIf(count == 0, this);
                int previous = Interlocked.CompareExchange(ref references, checked(count + 1), count);
                if (previous == count)
                    return;
                count = previous;
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref references) == 0)
                Dispose();
        }

        protected override bool ReleaseHandle()
        {
            NativeMemory.Free((void*)handle);
            GC.RemoveMemoryPressure(bytes);
            Interlocked.Add(ref allocatedBytes, -bytes);
            return true;
        }
    }
}