using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using LightStudio.LightPlayer.Controls;
using WSnip.App.Rendering;
using WSnip.Core.Editing;
using WSnip.Core.Imaging;
using PixelRect = WSnip.Core.Imaging.PixelRect;
using Vector = Avalonia.Vector;

namespace WSnip.App.Editor;

public enum EditorTool
{
    Select,
    Pen,
    Highlighter,
    Eraser,
    Crop,
}

/// <summary>The image pixel under the pointer and where the pointer is in the canvas.</summary>
public readonly record struct PixelHover(int X, int Y, Point Position);

/// <summary>
/// Shows the snip being edited with zoom and pan, and turns pointer input into strokes, erasures
/// and a crop rectangle. Zoom 1 shows one image pixel per display pixel.
/// </summary>
public sealed class SnipCanvas : Control, Avalonia.Rendering.ICustomHitTest
{
    public static readonly StyledProperty<EditorDocument?> DocumentProperty =
        AvaloniaProperty.Register<SnipCanvas, EditorDocument?>(nameof(Document));

    public static readonly StyledProperty<EditorTool> ToolProperty =
        AvaloniaProperty.Register<SnipCanvas, EditorTool>(nameof(Tool));

    public static readonly StyledProperty<ScRgb> StrokeColorProperty =
        AvaloniaProperty.Register<SnipCanvas, ScRgb>(nameof(StrokeColor), new ScRgb(1, 0, 0));

    public static readonly StyledProperty<double> StrokeSizeProperty =
        AvaloniaProperty.Register<SnipCanvas, double>(nameof(StrokeSize), 4);

    public static readonly StyledProperty<bool> ShowHdrProperty =
        AvaloniaProperty.Register<SnipCanvas, bool>(nameof(ShowHdr), true);

    public static readonly StyledProperty<SdrToneMapping> SdrMappingProperty =
        AvaloniaProperty.Register<SnipCanvas, SdrToneMapping>(nameof(SdrMapping));

    /// <summary>A click samples the pixel under the pointer instead of using the tool.</summary>
    public static readonly StyledProperty<bool> IsPickingColorProperty =
        AvaloniaProperty.Register<SnipCanvas, bool>(nameof(IsPickingColor));

    private const double ViewMargin = 24;
    private const double HandleSize = 8;
    private const double WheelZoomStep = 1.25;
    private const double DipsPerWheelDelta = 50;
    private const double ZoomAnimationMs = 160;
    private static readonly IBrush CropDim = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
    private static readonly IPen CropPen = new Pen(Brushes.White, 1.5);
    private static readonly Cursor GrabCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor EraserCursor = new(StandardCursorType.Hand);

    private readonly Glide zoomGlide;
    private double? zoomTarget;
    private Vector2 zoomAnchor;
    private Point zoomFocus;
    private double zoom = 1;
    private bool fit = true;
    private Vector offset;
    private IPointer? panPointer;
    private Point? panStart;
    private Vector panOrigin;
    private IPointer? tapPointer;
    private Rect tapBounds;
    private bool previousWasTap;
    private bool doubleTapPending;
    private Point? pointerPosition;
    private List<Vector2>? activePoints;
    private HashSet<AnnotationStroke>? erased;
    private PixelRect? crop;
    private CropDrag cropDrag;
    private Point cropStart;
    private PixelRect cropOrigin;
    private SurfaceColorInfo surface;
    private SurfaceColorInfo pendingSurface;
    private int surfacePosted;
    private PixelHover? hover;

    static SnipCanvas()
    {
        AffectsRender<SnipCanvas>(ToolProperty, ShowHdrProperty, SdrMappingProperty);
        FocusableProperty.OverrideDefaultValue<SnipCanvas>(true);
        ClipToBoundsProperty.OverrideDefaultValue<SnipCanvas>(true);
    }

    public SnipCanvas()
    {
        zoomGlide = new Glide(this, ApplyAnimatedZoom) { Completed = () => zoomTarget = null };
        DoubleTapped += OnDoubleTapped;
        PointerTouchPadGestureMagnify += OnTouchpadMagnify;
        DetachedFromVisualTree += (_, _) => StopZoomAnimation();
        EffectiveViewportChanged += (_, e) =>
        {
            Rect visible = e.EffectiveViewport.Intersect(new Rect(Bounds.Size));
            if (!IsEffectivelyVisible || visible.Width <= 0 || visible.Height <= 0)
                StopZoomAnimation();
        };
    }

    public event EventHandler? ViewChanged;

    public event EventHandler? SurfaceChanged;

    public event EventHandler? CropChanged;

    /// <summary>Raised when the pointer moves onto another image pixel, or off the image (null).</summary>
    public event EventHandler<PixelHover?>? HoverChanged;

    /// <summary>Raised with the image pixel clicked while picking a color.</summary>
    public event EventHandler<PixelHover>? PixelPicked;

    public EditorDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public EditorTool Tool
    {
        get => GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    public ScRgb StrokeColor
    {
        get => GetValue(StrokeColorProperty);
        set => SetValue(StrokeColorProperty, value);
    }

    /// <summary>Stroke width in device-independent pixels at the current zoom.</summary>
    public double StrokeSize
    {
        get => GetValue(StrokeSizeProperty);
        set => SetValue(StrokeSizeProperty, value);
    }

    public bool ShowHdr
    {
        get => GetValue(ShowHdrProperty);
        set => SetValue(ShowHdrProperty, value);
    }

    public SdrToneMapping SdrMapping
    {
        get => GetValue(SdrMappingProperty);
        set => SetValue(SdrMappingProperty, value);
    }

    public bool IsPickingColor
    {
        get => GetValue(IsPickingColorProperty);
        set => SetValue(IsPickingColorProperty, value);
    }

    /// <summary>The pixel under the pointer, if the pointer is over the image.</summary>
    public PixelHover? Hover => hover;

    public double Zoom => fit ? FitZoom() : zoom;

    public bool IsFit => fit;

    /// <summary>The color capabilities of the surface the snip was last drawn on.</summary>
    public SurfaceColorInfo Surface => surface;

    /// <summary>The pending crop rectangle in image pixels, while the crop tool is active.</summary>
    public PixelRect? CropRegion => crop;

    public void ZoomBy(double factor) => AnimateZoom((zoomTarget ?? Zoom) * factor, Center);

    public void ZoomToFit() => AnimateZoom(FitZoom(), Center);

    public void ZoomToActualSize() => AnimateZoom(1, Center);

    public void ResetCrop()
    {
        crop = Document is { } document ? new PixelRect(0, 0, document.Width, document.Height) : null;
        CropChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>The image is drawn by a custom operation, so the whole control claims pointer input.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty)
        {
            StopZoomAnimation();
            CancelTap();
            panPointer?.Capture(null);
            panPointer = null;
            panStart = null;
            if (change.OldValue is EditorDocument old)
                old.Changed -= OnDocumentChanged;
            if (change.NewValue is EditorDocument document)
                document.Changed += OnDocumentChanged;
            fit = true;
            offset = default;
            crop = null;
            SetHover(null);
            OnViewChanged();
        }
        else if (change.Property == ToolProperty || change.Property == IsPickingColorProperty)
        {
            CancelTap();
            if (change.Property == ToolProperty && Tool == EditorTool.Crop)
                ResetCrop();
            else if (change.Property == ToolProperty && crop is not null)
            {
                crop = null;
                CropChanged?.Invoke(this, EventArgs.Empty);
            }

            UpdateCursor();
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size size = base.ArrangeOverride(finalSize);
        ClampOffset();
        Dispatcher.UIThread.Post(() => ViewChanged?.Invoke(this, EventArgs.Empty));
        return size;
    }

    public override void Render(DrawingContext context)
    {
        if (Document is not { } document)
            return;

        Rect imageRect = ImageRect();
        SharedImage image = ShowHdr && surface.ExtendedRange
            ? document.GetHdrImage((float)surface.Headroom)
            : document.GetSdrImage(SdrMapping);
        IReadOnlyList<AnnotationStroke> strokes = document.Current.Strokes;
        if (erased is { Count: > 0 })
            strokes = strokes.Where(s => !erased.Contains(s)).ToArray();
        if (activePoints is { Count: > 0 })
            strokes = [.. strokes, CreateStroke(activePoints)];

        double physicalZoom = Zoom;
        context.Custom(new SnipDrawOperation(new Rect(Bounds.Size), image, imageRect, strokes, physicalZoom < 1.5, ObserveSurface)
        {
            Checkerboard = document.Current.Statistics.VisiblePixels < (long)document.Width * document.Height,
        });

        if (Tool == EditorTool.Crop && crop is { } region)
            DrawCrop(context, imageRect, region);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Document is not { } document)
            return;
        if (panPointer is not null)
            return;
        StopZoomAnimation();
        Focus();
        PointerPoint point = e.GetCurrentPoint(this);
        pointerPosition = point.Position;
        bool left = point.Properties.IsLeftButtonPressed;
        doubleTapPending = false;
        if (left && Tool == EditorTool.Select && !IsPickingColor)
        {
            tapPointer = e.Pointer;
            Size tapSize = Application.Current?.PlatformSettings?.GetTapSize(e.Pointer.Type) ?? new Size(4, 4);
            tapBounds = new Rect(point.Position, default(Size)).Inflate(new Thickness(tapSize.Width, tapSize.Height));
        }
        else
        {
            CancelTap();
        }

        if (left && IsPickingColor)
        {
            if (PixelAt(point.Position) is { } picked)
                PixelPicked?.Invoke(this, picked);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsMiddleButtonPressed || (left && Tool == EditorTool.Select))
        {
            if (!CanPan)
                return;
            panPointer = e.Pointer;
            panStart = point.Position;
            panOrigin = offset;
            SetHover(null);
            UpdateCursor();
        }
        else if (left && Tool is EditorTool.Pen or EditorTool.Highlighter)
        {
            activePoints = [ToImage(point.Position)];
        }
        else if (left && Tool == EditorTool.Eraser)
        {
            erased = [];
            EraseAt(document, point.Position);
        }
        else if (left && Tool == EditorTool.Crop && crop is { } region)
        {
            cropDrag = HitCrop(region, point.Position);
            cropStart = point.Position;
            cropOrigin = region;
            if (cropDrag == CropDrag.None)
            {
                cropDrag = CropDrag.New;
                Vector2 start = ToImage(point.Position);
                cropOrigin = new PixelRect((int)Math.Round(start.X), (int)Math.Round(start.Y), 0, 0);
            }
        }
        else
        {
            return;
        }

        e.Pointer.Capture(this);
        e.Handled = Tool != EditorTool.Select || point.Properties.IsMiddleButtonPressed;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point position = e.GetPosition(this);
        pointerPosition = position;
        if (ReferenceEquals(tapPointer, e.Pointer) && !tapBounds.ContainsExclusive(position))
            CancelTap();
        if (panStart is null)
            SetHover(PixelAt(position));
        if (panStart is { } start)
        {
            if (!ReferenceEquals(panPointer, e.Pointer))
                return;
            offset = panOrigin + (position - start);
            ClampOffset();
            OnViewChanged();
            e.Handled = true;
        }
        else if (activePoints is not null)
        {
            Vector2 next = ToImage(position);
            if (Vector2.Distance(next, activePoints[^1]) * ImageScale() >= 1.5)
            {
                activePoints.Add(next);
                InvalidateVisual();
            }
        }
        else if (erased is not null && Document is { } document)
        {
            EraseAt(document, position);
        }
        else if (cropDrag != CropDrag.None && Document is { } cropped)
        {
            crop = DragCrop(cropped, position);
            CropChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
        else if (!IsPickingColor && Tool == EditorTool.Crop && crop is { } region)
        {
            Cursor = HitCrop(region, position) switch
            {
                CropDrag.Left or CropDrag.Right => new Cursor(StandardCursorType.SizeWestEast),
                CropDrag.Top or CropDrag.Bottom => new Cursor(StandardCursorType.SizeNorthSouth),
                CropDrag.TopLeft or CropDrag.BottomRight => new Cursor(StandardCursorType.TopLeftCorner),
                CropDrag.TopRight or CropDrag.BottomLeft => new Cursor(StandardCursorType.TopRightCorner),
                CropDrag.Move => new Cursor(StandardCursorType.SizeAll),
                _ => new Cursor(StandardCursorType.Cross),
            };
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (panPointer is not null && !ReferenceEquals(panPointer, e.Pointer))
            return;
        bool tapped = ReferenceEquals(tapPointer, e.Pointer) &&
            tapBounds.ContainsExclusive(e.GetPosition(this)) && Tool == EditorTool.Select && !IsPickingColor;
        bool toggleZoom = tapped && doubleTapPending;
        CancelTap();
        EditorDocument? document = Document;
        if (panStart is not null)
        {
            panPointer = null;
            panStart = null;
            UpdateCursor();
            SetHover(PixelAt(e.GetPosition(this)));
        }
        else if (activePoints is { } points && document is not null)
        {
            activePoints = null;
            document.AddStroke(CreateStroke(points));
        }
        else if (erased is { } removed && document is not null)
        {
            erased = null;
            document.Erase(removed);
        }
        else if (cropDrag != CropDrag.None)
        {
            cropDrag = CropDrag.None;
            if (crop is { Width: < 2 } or { Height: < 2 })
                ResetCrop();
        }

        e.Pointer.Capture(null);
        previousWasTap = tapped && !toggleZoom;
        if (toggleZoom)
        {
            AnimateZoom(IsFit ? 1 : FitZoom(), e.GetPosition(this));
            e.Handled = true;
        }
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (ReferenceEquals(tapPointer, e.Pointer))
            CancelTap();
        if (ReferenceEquals(panPointer, e.Pointer))
        {
            panPointer = null;
            panStart = null;
            UpdateCursor();
            SetHover(null);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        pointerPosition = null;
        SetHover(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Document is null || e.Delta == default)
            return;
        pointerPosition = e.GetPosition(this);
        if (e.IsTouchpad && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            StopZoomAnimation();
            offset += e.Delta * DipsPerWheelDelta;
            ClampOffset();
            OnViewChanged();
        }
        else if (e.Delta.Y != 0)
        {
            if (e.IsTouchpad)
                SetZoom(Zoom * Math.Pow(WheelZoomStep, e.Delta.Y), e.GetPosition(this));
            else
                AnimateZoom((zoomTarget ?? Zoom) * Math.Pow(WheelZoomStep, e.Delta.Y), e.GetPosition(this));
        }
        else
            return;

        SetHover(PixelAt(e.GetPosition(this)));
        e.Handled = true;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Document is null || Tool != EditorTool.Select || IsPickingColor)
            return;

        doubleTapPending = previousWasTap && tapPointer is not null;
        e.Handled = true;
    }

    private void CancelTap()
    {
        tapPointer = null;
        previousWasTap = false;
        doubleTapPending = false;
    }

    private void OnTouchpadMagnify(object? sender, PointerDeltaEventArgs e)
    {
        if (Document is null || !double.IsFinite(e.Delta.Y) || e.Delta.Y <= -1)
            return;

        SetZoom(Zoom * (1 + e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        if (Tool == EditorTool.Crop)
            ResetCrop();
        ClampOffset();
        OnViewChanged();
    }

    private void OnViewChanged()
    {
        UpdateCursor();
        if (panStart is null && pointerPosition is { } position)
            SetHover(PixelAt(position));
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCursor()
    {
        Cursor = Tool switch
        {
            _ when panStart is not null => GrabCursor,
            _ when IsPickingColor => CrossCursor,
            EditorTool.Pen or EditorTool.Highlighter or EditorTool.Crop => CrossCursor,
            EditorTool.Eraser => EraserCursor,
            _ when CanPan => GrabCursor,
            _ => Cursor.Default,
        };
    }

    /// <summary>The image pixel at a canvas position, or null outside the image.</summary>
    private PixelHover? PixelAt(Point position)
    {
        if (Document is not { } document)
            return null;
        Vector2 point = ToImage(position);
        int x = (int)MathF.Floor(point.X), y = (int)MathF.Floor(point.Y);
        return x >= 0 && y >= 0 && x < document.Width && y < document.Height ? new PixelHover(x, y, position) : null;
    }

    private void SetHover(PixelHover? next)
    {
        PixelHover? previous = hover;
        hover = next;
        if (previous is null != next is null || (next is { } n && previous is { } p && (n.X != p.X || n.Y != p.Y || n.Position != p.Position)))
            HoverChanged?.Invoke(this, next);
    }

    // Called on the render thread; hands the surface description back to the UI thread when it changes.
    private void ObserveSurface(SurfaceColorInfo info)
    {
        if (info == pendingSurface && Volatile.Read(ref surfacePosted) == 1)
            return;
        pendingSurface = info;
        Volatile.Write(ref surfacePosted, 1);
        Dispatcher.UIThread.Post(() =>
        {
            if (surface == info)
                return;
            surface = info;
            InvalidateVisual();
            SurfaceChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private AnnotationStroke CreateStroke(IReadOnlyList<Vector2> points)
    {
        bool highlighter = Tool == EditorTool.Highlighter;
        float width = (float)(StrokeSize / ImageScale());
        return new AnnotationStroke(highlighter ? AnnotationTool.Highlighter : AnnotationTool.Pen,
            StrokeColor, MathF.Max(1, width), points.ToArray());
    }

    private void EraseAt(EditorDocument document, Point position)
    {
        Vector2 point = ToImage(position);
        float tolerance = (float)(6 / ImageScale());
        foreach (AnnotationStroke stroke in document.Current.Strokes)
        {
            if (!erased!.Contains(stroke) && stroke.HitTest(point, tolerance))
            {
                erased.Add(stroke);
                InvalidateVisual();
            }
        }
    }

    /// <summary>Device-independent pixels per image pixel.</summary>
    private double ImageScale() => Zoom / Math.Max(0.1, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);

    private Point Center => new(Bounds.Width / 2, Bounds.Height / 2);

    private bool CanPan => Document is { } document &&
        (document.Width * ImageScale() - Bounds.Width > 1 || document.Height * ImageScale() - Bounds.Height > 1);

    private double FitZoom()
    {
        if (Document is not { } document || Bounds.Width <= 0 || Bounds.Height <= 0)
            return 1;
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        double available = Math.Min(
            Math.Max(1, Bounds.Width - 2 * ViewMargin) * scaling / document.Width,
            Math.Max(1, Bounds.Height - 2 * ViewMargin) * scaling / document.Height);
        return Math.Min(1, available);
    }

    private Rect ImageRect()
    {
        if (Document is not { } document)
            return default;
        double scale = ImageScale();
        var size = new Size(document.Width * scale, document.Height * scale);
        var center = new Point(Bounds.Width / 2 + offset.X, Bounds.Height / 2 + offset.Y);
        return new Rect(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height);
    }

    private Vector2 ToImage(Point point)
    {
        Rect rect = ImageRect();
        double scale = ImageScale();
        return new Vector2((float)((point.X - rect.X) / scale), (float)((point.Y - rect.Y) / scale));
    }

    private Point ToView(double x, double y)
    {
        Rect rect = ImageRect();
        double scale = ImageScale();
        return new Point(rect.X + x * scale, rect.Y + y * scale);
    }

    private void SetZoom(double value, Point anchor)
    {
        StopZoomAnimation();
        if (Document is not null)
            ApplyZoom(value, ToImage(anchor), anchor);
    }

    private void AnimateZoom(double value, Point anchor)
    {
        if (Document is null)
            return;
        value = Math.Clamp(value, FitZoom(), 32);
        if (zoomTarget is { } target && Math.Abs(value - target) < 1e-6)
            return;

        StopZoomAnimation();
        if (TopLevel.GetTopLevel(this) is null || !IsEffectivelyVisible || Opacity <= 0)
        {
            SetZoom(value, anchor);
            return;
        }
        if (Math.Abs(value - Zoom) < 1e-6)
            return;

        zoomTarget = value;
        zoomAnchor = ToImage(anchor);
        zoomFocus = anchor;
        zoomGlide.Start(Zoom, value, ZoomAnimationMs);
        OnViewChanged();
    }

    private void ApplyAnimatedZoom(double value)
    {
        if (Document is null || !IsEffectivelyVisible || Opacity <= 0)
        {
            StopZoomAnimation();
            return;
        }

        ApplyZoom(value, zoomAnchor, zoomFocus);
    }

    private void StopZoomAnimation()
    {
        zoomGlide.Stop();
        zoomTarget = null;
    }

    private void ApplyZoom(double value, Vector2 imagePoint, Point anchor)
    {
        if (Document is null)
            return;
        double minimum = FitZoom();
        value = Math.Clamp(value, minimum, 32);
        fit = Math.Abs(value - minimum) < 1e-6;
        zoom = fit ? minimum : value;
        double scale = ImageScale();
        var size = new Size(Document.Width * scale, Document.Height * scale);
        var topLeft = new Point(anchor.X - imagePoint.X * scale, anchor.Y - imagePoint.Y * scale);
        offset = new Vector(topLeft.X + size.Width / 2 - Bounds.Width / 2, topLeft.Y + size.Height / 2 - Bounds.Height / 2);
        ClampOffset();
        OnViewChanged();
    }

    private void ClampOffset()
    {
        if (Document is not { } document)
            return;
        double scale = ImageScale();
        double width = document.Width * scale, height = document.Height * scale;
        double limitX = Math.Max(0, (width - Bounds.Width) / 2);
        double limitY = Math.Max(0, (height - Bounds.Height) / 2);
        offset = new Vector(
            Math.Clamp(offset.X, -limitX, limitX),
            Math.Clamp(offset.Y, -limitY, limitY));
    }

    private enum CropDrag
    {
        None,
        New,
        Move,
        Left,
        Top,
        Right,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    private CropDrag HitCrop(PixelRect region, Point point)
    {
        Point topLeft = ToView(region.X, region.Y);
        Point bottomRight = ToView(region.Right, region.Bottom);
        bool nearLeft = Math.Abs(point.X - topLeft.X) <= HandleSize;
        bool nearRight = Math.Abs(point.X - bottomRight.X) <= HandleSize;
        bool nearTop = Math.Abs(point.Y - topLeft.Y) <= HandleSize;
        bool nearBottom = Math.Abs(point.Y - bottomRight.Y) <= HandleSize;
        bool withinX = point.X >= topLeft.X - HandleSize && point.X <= bottomRight.X + HandleSize;
        bool withinY = point.Y >= topLeft.Y - HandleSize && point.Y <= bottomRight.Y + HandleSize;
        if (!withinX || !withinY)
            return CropDrag.None;
        return (nearLeft, nearTop, nearRight, nearBottom) switch
        {
            (true, true, _, _) => CropDrag.TopLeft,
            (_, true, true, _) => CropDrag.TopRight,
            (true, _, _, true) => CropDrag.BottomLeft,
            (_, _, true, true) => CropDrag.BottomRight,
            (true, _, _, _) => CropDrag.Left,
            (_, true, _, _) => CropDrag.Top,
            (_, _, true, _) => CropDrag.Right,
            (_, _, _, true) => CropDrag.Bottom,
            // A frame around the whole image has nowhere to move, so dragging inside it draws a new one.
            _ when Document is { } document && region == new PixelRect(0, 0, document.Width, document.Height) => CropDrag.None,
            _ => CropDrag.Move,
        };
    }

    private PixelRect DragCrop(EditorDocument document, Point position)
    {
        double scale = ImageScale();
        int dx = (int)Math.Round((position.X - cropStart.X) / scale);
        int dy = (int)Math.Round((position.Y - cropStart.Y) / scale);
        int left = cropOrigin.X, top = cropOrigin.Y, right = cropOrigin.Right, bottom = cropOrigin.Bottom;
        switch (cropDrag)
        {
            case CropDrag.Move:
                dx = Math.Clamp(dx, -left, document.Width - right);
                dy = Math.Clamp(dy, -top, document.Height - bottom);
                return cropOrigin.Offset(dx, dy);
            case CropDrag.New:
                right = left + dx;
                bottom = top + dy;
                break;
            default:
                if (cropDrag is CropDrag.Left or CropDrag.TopLeft or CropDrag.BottomLeft)
                    left += dx;
                if (cropDrag is CropDrag.Right or CropDrag.TopRight or CropDrag.BottomRight)
                    right += dx;
                if (cropDrag is CropDrag.Top or CropDrag.TopLeft or CropDrag.TopRight)
                    top += dy;
                if (cropDrag is CropDrag.Bottom or CropDrag.BottomLeft or CropDrag.BottomRight)
                    bottom += dy;
                break;
        }

        return PixelRect.FromEdges(Math.Min(left, right), Math.Min(top, bottom), Math.Max(left, right), Math.Max(top, bottom))
            .Intersect(new PixelRect(0, 0, document.Width, document.Height));
    }

    private void DrawCrop(DrawingContext context, Rect imageRect, PixelRect region)
    {
        var rect = new Rect(ToView(region.X, region.Y), ToView(region.Right, region.Bottom));
        var dimmed = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dimmed.Children.Add(new RectangleGeometry(imageRect));
        dimmed.Children.Add(new RectangleGeometry(rect));
        context.DrawGeometry(CropDim, null, dimmed);
        context.DrawRectangle(null, CropPen, rect);
        foreach (Point handle in new[]
        {
            rect.TopLeft, rect.TopRight, rect.BottomLeft, rect.BottomRight,
            new Point(rect.Center.X, rect.Top), new Point(rect.Center.X, rect.Bottom),
            new Point(rect.Left, rect.Center.Y), new Point(rect.Right, rect.Center.Y),
        })
        {
            context.DrawRectangle(Brushes.White, new Pen(Brushes.Black, 1),
                new Rect(handle.X - HandleSize / 2, handle.Y - HandleSize / 2, HandleSize, HandleSize));
        }
    }
}
