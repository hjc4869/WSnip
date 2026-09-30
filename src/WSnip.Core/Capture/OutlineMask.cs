using SkiaSharp;
using WSnip.Core.Imaging;

namespace WSnip.Core.Capture;

/// <summary>Rasterizes a freeform outline into an antialiased coverage mask.</summary>
internal static class OutlineMask
{
    public static byte[] Rasterize(PixelRect region, IReadOnlyList<(double X, double Y)> outline)
    {
        var info = new SKImageInfo(region.Width, region.Height, SKColorType.Alpha8, SKAlphaType.Premul);
        var mask = new byte[region.Width * region.Height];
        unsafe
        {
            fixed (byte* pixels = mask)
            {
                using var surface = SKSurface.Create(info, (IntPtr)pixels, region.Width);
                if (surface is null)
                    throw new InvalidOperationException("The outline mask could not be allocated.");

                using var path = new SKPath { FillType = SKPathFillType.Winding };
                path.MoveTo((float)(outline[0].X - region.X), (float)(outline[0].Y - region.Y));
                for (int i = 1; i < outline.Count; i++)
                    path.LineTo((float)(outline[i].X - region.X), (float)(outline[i].Y - region.Y));
                path.Close();

                using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.DrawPath(path, paint);
                surface.Flush();
            }
        }

        return mask;
    }
}
