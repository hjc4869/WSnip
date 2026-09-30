using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using WSnip.Core.Imaging;

namespace WSnip.App.Rendering;

/// <summary>
/// A disc of one scRGB color, painted through Skia in linear sRGB so wide-gamut and HDR colors
/// reach an extended-range surface as they are; SDR surfaces show the clipped color.
/// </summary>
public sealed class ColorSwatch : Control
{
    public static readonly StyledProperty<ScRgb> ColorProperty =
        AvaloniaProperty.Register<ColorSwatch, ScRgb>(nameof(Color), ScRgb.White);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<ColorSwatch, IBrush?>(nameof(Stroke));

    static ColorSwatch() => AffectsRender<ColorSwatch>(ColorProperty, StrokeProperty);

    public ScRgb Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>Outline that keeps light and dark colors apart from the background.</summary>
    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;
        context.Custom(new ColorDrawOperation(bounds, Color));
        if (Stroke is { } stroke)
            context.DrawEllipse(null, new Pen(stroke, 1), bounds.Center, bounds.Width / 2 - 0.5, bounds.Height / 2 - 0.5);
    }

    private sealed class ColorDrawOperation(Rect bounds, ScRgb color) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public ScRgb Color => color;

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => other is ColorDrawOperation o && o.Bounds == Bounds && o.Color == Color;

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
                return;
            using ISkiaSharpApiLease lease = feature.Lease();
            using SKColorSpace linear = SKColorSpace.CreateSrgbLinear();
            using var paint = new SKPaint { IsAntialias = true };
            paint.SetColor(new SKColorF(color.R, color.G, color.B, 1), linear);
            lease.SkCanvas.DrawOval(new SKRect((float)bounds.X, (float)bounds.Y, (float)bounds.Right, (float)bounds.Bottom), paint);
        }
    }
}
