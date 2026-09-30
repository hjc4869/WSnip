using SkiaSharp;
using WSnip.Core.Encoding;
using WSnip.Core.Imaging;

namespace WSnip.App.Rendering;

/// <summary>
/// An immutable Skia image shared by reference count between the UI thread, which creates it, and
/// draw operations running on the render thread.
/// </summary>
/// <remarks>
/// Raster images are drawn directly on GPU canvases; Skia keeps the uploaded texture in its resource
/// cache keyed by the image, so redraws for zooming and panning do not upload again.
/// </remarks>
public sealed class SharedImage
{
    private SKImage? image;
    private int references = 1;

    private SharedImage(SKImage image, bool isHdr)
    {
        this.image = image;
        IsHdr = isHdr;
    }

    public SKImage Image => image ?? throw new ObjectDisposedException(nameof(SharedImage));

    /// <summary>Whether the pixels are linear and relative to SDR white, so values above one are highlights.</summary>
    public bool IsHdr { get; }

    public int Width => Image.Width;

    public int Height => Image.Height;

    public SharedImage AddRef()
    {
        Interlocked.Increment(ref references);
        return this;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref references) == 0)
        {
            image?.Dispose();
            image = null;
        }
    }

    /// <summary>Wraps a relative linear scRGB image, tagged as linear sRGB so values above one stay highlights.</summary>
    public static SharedImage FromHdr(HdrImage source) => FromLinear(source, 1);

    /// <summary>
    /// Wraps composition pixels as captured, where <paramref name="whiteScale"/> is SDR white. The
    /// scale goes into the color space, so Skia normalizes while drawing and no copy is needed.
    /// </summary>
    public static unsafe SharedImage FromLinear(HdrImage source, double whiteScale)
    {
        float[] m = SKColorSpaceXyz.Srgb.Values;
        float k = (float)(1 / whiteScale);
        using SKColorSpace space = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Linear,
            new SKColorSpaceXyz(m[0] * k, m[1] * k, m[2] * k, m[3] * k, m[4] * k, m[5] * k, m[6] * k, m[7] * k, m[8] * k));
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.RgbaF16, SKAlphaType.Unpremul, space);
        fixed (Half* pixels = source.Pixels)
        {
            SKImage image = SKImage.FromPixelCopy(info, (IntPtr)pixels, source.Width * 8)
                ?? throw new InvalidOperationException("The image could not be prepared for display.");
            return new SharedImage(image, isHdr: true);
        }
    }

    /// <summary>Wraps the 8-bit SDR rendition, tagged with its primaries.</summary>
    public static unsafe SharedImage FromSdr(Rendition rendition)
    {
        using SKColorSpace space = rendition.Primaries switch
        {
            ColorPrimaries.DisplayP3 => SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3),
            ColorPrimaries.Bt2020 => SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.Rec2020),
            _ => SKColorSpace.CreateSrgb(),
        };
        var info = new SKImageInfo(rendition.Width, rendition.Height, SKColorType.Rgba8888,
            rendition.HasAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque, space);
        fixed (byte* pixels = rendition.Sdr)
        {
            SKImage image = SKImage.FromPixelCopy(info, (IntPtr)pixels, rendition.Width * 4)
                ?? throw new InvalidOperationException("The image could not be prepared for display.");
            return new SharedImage(image, isHdr: false);
        }
    }
}
