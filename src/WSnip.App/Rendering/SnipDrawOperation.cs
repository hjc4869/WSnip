using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using WSnip.Core.Editing;

namespace WSnip.App.Rendering;

/// <summary>What the surface a snip was last drawn into can show.</summary>
/// <param name="ExtendedRange">Values above SDR white and outside sRGB reach the display.</param>
/// <param name="ReferenceWhiteScale">Surface units of SDR white.</param>
/// <param name="Headroom">Peak relative to SDR white, or 1 when unknown.</param>
public readonly record struct SurfaceColorInfo(bool ExtendedRange, double ReferenceWhiteScale, double Headroom);

/// <summary>
/// Draws a snip, and optionally its annotations, straight into the Skia surface so relative HDR
/// pixels reach the extended-range composition surface untouched.
/// </summary>
/// <remarks>
/// On an extended-linear surface Skia maps linear sRGB 1.0 to the display's SDR white, so a relative
/// image tagged as linear sRGB lands exactly at the brightness it was captured with. Multiply-blended
/// highlighter strokes are not scale invariant, so their color is divided by the white scale to blend
/// as they do when the annotations are burnt into the saved image.
/// </remarks>
public sealed class SnipDrawOperation : ICustomDrawOperation
{
    private readonly SharedImage image;
    private readonly Rect destination;
    private readonly IReadOnlyList<AnnotationStroke> strokes;
    private readonly bool smooth;
    private readonly Action<SurfaceColorInfo>? surfaceObserved;

    public SnipDrawOperation(Rect bounds, SharedImage image, Rect destination, IReadOnlyList<AnnotationStroke> strokes,
        bool smooth, Action<SurfaceColorInfo>? surfaceObserved)
    {
        Bounds = bounds;
        this.image = image.AddRef();
        this.destination = destination;
        this.strokes = strokes;
        this.smooth = smooth;
        this.surfaceObserved = surfaceObserved;
    }

    /// <summary>Draws a checkerboard under the image so transparent areas read as such.</summary>
    public bool Checkerboard { get; init; }

    public Rect Bounds { get; }

    public bool HitTest(Point p) => false;

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Dispose() => image.Release();

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
            return;

        using ISkiaSharpApiLease lease = feature.Lease();
        SKCanvas canvas = lease.SkCanvas;
        PlatformSurfaceColorVolume? volume = lease.PreferredColorVolume;
        bool extended = lease.ColorFormat.IsExtendedRange;
        double whiteScale = extended ? volume?.ReferenceWhiteScale ?? 1 : 1;
        double headroom = extended ? volume?.HeadroomRatio ?? 1 : 1;
        surfaceObserved?.Invoke(new SurfaceColorInfo(extended, whiteScale, headroom));

        canvas.Save();
        try
        {
            canvas.ClipRect(ToSk(Bounds));
            if (Checkerboard)
                DrawCheckerboard(canvas, ToSk(destination));
            SKSamplingOptions sampling = smooth
                ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
                : new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
            using (var paint = new SKPaint { IsAntialias = false })
                canvas.DrawImage(image.Image, new SKRect(0, 0, image.Width, image.Height), ToSk(destination), sampling, paint);

            if (strokes.Count == 0)
                return;

            canvas.ClipRect(ToSk(destination));
            canvas.Translate((float)destination.X, (float)destination.Y);
            canvas.Scale((float)(destination.Width / image.Width), (float)(destination.Height / image.Height));
            using SKColorSpace linear = SKColorSpace.CreateSrgbLinear();
            float inverse = (float)(1 / whiteScale);
            foreach (AnnotationStroke stroke in strokes)
            {
                // Over the SDR rendition, pens show as SDR files will have them.
                AnnotationStroke drawn = image.IsHdr ? stroke : stroke with { Color = stroke.Color.ClampToSdr() };
                using SKPath path = drawn.ToPath();
                using SKPaint paint = drawn.CreatePaint(linear, drawn.Tool == AnnotationTool.Highlighter ? inverse : 1);
                canvas.DrawPath(path, paint);
            }
        }
        finally
        {
            canvas.Restore();
        }
    }

    private static SKRect ToSk(Rect rect) => new((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom);

    private static void DrawCheckerboard(SKCanvas canvas, SKRect area)
    {
        using var tile = new SKBitmap(16, 16);
        using (var tileCanvas = new SKCanvas(tile))
        {
            tileCanvas.Clear(new SKColor(0xFF, 0xFF, 0xFF));
            using var dark = new SKPaint { Color = new SKColor(0xCC, 0xCC, 0xCC) };
            tileCanvas.DrawRect(0, 0, 8, 8, dark);
            tileCanvas.DrawRect(8, 8, 8, 8, dark);
        }

        using SKShader shader = SKShader.CreateBitmap(tile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
            SKMatrix.CreateTranslation(area.Left, area.Top));
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(area, paint);
    }
}
