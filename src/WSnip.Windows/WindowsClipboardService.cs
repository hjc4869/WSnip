using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WSnip.Core.Platform;
using WSnip.Windows.Interop;

using WSnip.Core.Strings;

namespace WSnip.Windows;

/// <summary>
/// Puts images on the clipboard as PNG, which keeps transparency, and as a DIB, from which the system
/// derives CF_DIBV5 and CF_BITMAP. The data is written right away rather than rendered on request,
/// so it outlives the app.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsClipboardService : IClipboardService
{
    private const uint DibFormat = 8;
    private const uint MoveableMemory = 0x0002;
    private static readonly uint PngFormat = Native.RegisterClipboardFormat("PNG");

    public void SetImage(ClipboardImage image, nint ownerWindow)
    {
        nint dib = 0, png = 0;
        try
        {
            dib = CreateDib(image);
            png = CreateGlobal(image.Png);
            Open(ownerWindow);
            try
            {
                if (!Native.EmptyClipboard())
                    throw new Win32Exception();
                Set(DibFormat, ref dib);
                if (PngFormat != 0)
                    Set(PngFormat, ref png);
            }
            finally
            {
                Native.CloseClipboard();
            }
        }
        finally
        {
            if (dib != 0)
                Native.GlobalFree(dib);
            if (png != 0)
                Native.GlobalFree(png);
        }
    }

    private static void Open(nint owner)
    {
        // Another app may hold the clipboard for a moment.
        for (int attempt = 1; !Native.OpenClipboard(owner); attempt++)
        {
            if (attempt == 10)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), AppStrings.ClipboardBusy);
            Thread.Sleep(20);
        }
    }

    private static void Set(uint format, ref nint memory)
    {
        if (Native.SetClipboardData(format, memory) == 0)
            throw new Win32Exception();

        // The clipboard owns the memory from here on.
        memory = 0;
    }

    /// <summary>
    /// A bottom-up 32-bit DIB. Apps treat its fourth byte inconsistently, so transparent areas are
    /// composited over white and the image is written opaque.
    /// </summary>
    private static nint CreateDib(ClipboardImage image)
    {
        int width = image.Width, height = image.Height;
        long stride = (long)width * 4;
        nint memory = Allocate((nuint)(sizeof(BitmapInfoHeader) + stride * height));
        byte* target = (byte*)Native.GlobalLock(memory);
        try
        {
            *(BitmapInfoHeader*)target = new BitmapInfoHeader
            {
                Size = (uint)sizeof(BitmapInfoHeader),
                Width = width,
                Height = height,
                Planes = 1,
                BitCount = 32,
                SizeImage = (uint)(stride * height),
            };

            byte* pixels = target + sizeof(BitmapInfoHeader);
            fixed (byte* source = image.Rgba)
            {
                for (int y = 0; y < height; y++)
                {
                    byte* from = source + y * stride;
                    byte* to = pixels + (height - 1 - y) * stride;
                    for (int x = 0; x < width; x++, from += 4, to += 4)
                    {
                        int alpha = image.HasAlpha ? from[3] : 255;
                        int white = 255 * (255 - alpha) + 127;
                        to[0] = (byte)((from[2] * alpha + white) / 255);
                        to[1] = (byte)((from[1] * alpha + white) / 255);
                        to[2] = (byte)((from[0] * alpha + white) / 255);
                        to[3] = 255;
                    }
                }
            }
        }
        finally
        {
            Native.GlobalUnlock(memory);
        }

        return memory;
    }

    private static nint CreateGlobal(ReadOnlySpan<byte> data)
    {
        nint memory = Allocate((nuint)data.Length);
        data.CopyTo(new Span<byte>(Native.GlobalLock(memory), data.Length));
        Native.GlobalUnlock(memory);
        return memory;
    }

    private static nint Allocate(nuint bytes)
    {
        nint memory = Native.GlobalAlloc(MoveableMemory, bytes);
        return memory != 0 ? memory : throw new OutOfMemoryException("The clipboard data could not be allocated.");
    }
}
