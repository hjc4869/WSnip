using System.Numerics;
using SkiaSharp;
using WSnip.Core.Imaging;

namespace WSnip.Core.Editing;

public enum AnnotationTool
{
    Pen,
    Highlighter,
}

/// <summary>
/// A freehand stroke in image pixel coordinates. Its color is relative linear scRGB like the snip,
/// so pens may be wide-gamut or brighter than SDR white; a highlighter multiplies and stays within
/// SDR sRGB.
/// </summary>
public sealed record AnnotationStroke(AnnotationTool Tool, ScRgb Color, float Width, IReadOnlyList<Vector2> Points)
{
    public AnnotationStroke Offset(Vector2 delta) => this with { Points = Points.Select(p => p + delta).ToArray() };

    /// <summary>Whether the stroke passes within a distance of a point.</summary>
    public bool HitTest(Vector2 point, float tolerance)
    {
        float reach = Width / 2 + tolerance;
        float reachSquared = reach * reach;
        if (Points.Count == 1)
            return Vector2.DistanceSquared(Points[0], point) <= reachSquared;
        for (int i = 1; i < Points.Count; i++)
        {
            if (DistanceSquaredToSegment(point, Points[i - 1], Points[i]) <= reachSquared)
                return true;
        }

        return false;
    }

    /// <summary>A smoothed path through the points, using midpoints as quadratic anchors.</summary>
    public SKPath ToPath()
    {
        var path = new SKPath();
        if (Points.Count == 0)
            return path;
        path.MoveTo(Points[0].X, Points[0].Y);
        if (Points.Count == 1)
        {
            path.LineTo(Points[0].X + 0.01f, Points[0].Y);
            return path;
        }

        for (int i = 1; i < Points.Count - 1; i++)
        {
            Vector2 middle = (Points[i] + Points[i + 1]) / 2;
            path.QuadTo(Points[i].X, Points[i].Y, middle.X, middle.Y);
        }

        path.LineTo(Points[^1].X, Points[^1].Y);
        return path;
    }

    /// <summary>The stroke color as drawn, in linear sRGB; highlighters are clipped to SDR.</summary>
    public ScRgb PaintColor => Tool == AnnotationTool.Highlighter ? Color.ClampToSdr() : Color;

    /// <summary>A paint for a canvas in <paramref name="linear"/> sRGB whose one is SDR white, scaled by <paramref name="scale"/>.</summary>
    public SKPaint CreatePaint(SKColorSpace linear, float scale = 1)
    {
        ScRgb color = PaintColor;
        var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Width,
            StrokeCap = Tool == AnnotationTool.Highlighter ? SKStrokeCap.Square : SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            BlendMode = Tool == AnnotationTool.Highlighter ? SKBlendMode.Multiply : SKBlendMode.SrcOver,
        };
        paint.SetColor(new SKColorF(color.R * scale, color.G * scale, color.B * scale, 1), linear);
        return paint;
    }

    private static float DistanceSquaredToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float length = ab.LengthSquared();
        float t = length > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / length, 0, 1) : 0;
        return Vector2.DistanceSquared(p, a + ab * t);
    }
}

/// <summary>Burns annotations into a relative HDR image, blending in linear light.</summary>
public static class AnnotationRenderer
{
    /// <summary>Returns an owned image, sharing the source when there is nothing to draw.</summary>
    public static HdrImage Render(HdrImage image, IReadOnlyList<AnnotationStroke> strokes)
    {
        if (strokes.Count == 0)
            return image.Share();

        using SKColorSpace linear = SKColorSpace.CreateSrgbLinear();
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.RgbaF16, SKAlphaType.Premul, linear);
        using var result = new HdrImage(image.Width, image.Height);
        ParallelRows.For(image.Height, y => Premultiply(image.ReadRow(y), result.Row(y)));

        // Draw straight into the result, temporarily premultiplied. There is no second full-size
        // Skia bitmap to copy back, and the source remains immutable for the editor/render thread.
        using (var surface = SKSurface.Create(info, result.Pointer, image.Width * 8)
            ?? throw new InvalidOperationException("The annotation surface could not be created."))
        {
            foreach (AnnotationStroke stroke in strokes)
            {
                using SKPath path = stroke.ToPath();
                using SKPaint paint = stroke.CreatePaint(linear);
                surface.Canvas.DrawPath(path, paint);
            }
            surface.Canvas.Flush();
        }

        Unpremultiply(result.Pixels, result.Pixels);
        return result.Share();
    }

    private static void Premultiply(ReadOnlySpan<Half> source, Span<Half> target)
    {
        for (int i = 0; i < source.Length; i += 4)
        {
            float alpha = (float)source[i + 3];
            target[i] = (Half)((float)source[i] * alpha);
            target[i + 1] = (Half)((float)source[i + 1] * alpha);
            target[i + 2] = (Half)((float)source[i + 2] * alpha);
            target[i + 3] = source[i + 3];
        }
    }

    private static void Unpremultiply(ReadOnlySpan<Half> source, Span<Half> target)
    {
        for (int i = 0; i < source.Length; i += 4)
        {
            float alpha = (float)source[i + 3];
            float inverse = alpha > 0 ? 1 / alpha : 0;
            target[i] = (Half)((float)source[i] * inverse);
            target[i + 1] = (Half)((float)source[i + 1] * inverse);
            target[i + 2] = (Half)((float)source[i + 2] * inverse);
            target[i + 3] = source[i + 3];
        }
    }
}
