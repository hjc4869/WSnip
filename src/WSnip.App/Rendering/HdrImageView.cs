using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WSnip.App.Rendering;

/// <summary>Draws a shared image stretched over the control, through the HDR-preserving draw operation.</summary>
public sealed class HdrImageView : Control
{
    private SharedImage? image;

    public SharedImage? Image
    {
        get => image;
        set
        {
            if (ReferenceEquals(image, value))
                return;
            SharedImage? next = value?.AddRef();
            image?.Release();
            image = next;
            InvalidateVisual();
        }
    }

    /// <summary>Filters when scaled; off for pixel-exact presentation.</summary>
    public bool Smooth { get; set; } = true;

    public override void Render(DrawingContext context)
    {
        if (image is null)
            return;
        var bounds = new Rect(Bounds.Size);
        context.Custom(new SnipDrawOperation(bounds, image, bounds, [], Smooth, surfaceObserved: null));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Image = null;
    }
}
