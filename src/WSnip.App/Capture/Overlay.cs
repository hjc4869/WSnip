using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WSnip.App.Rendering;
using WSnip.Core.Capture;
using PixelRect = WSnip.Core.Imaging.PixelRect;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace WSnip.App.Capture;

/// <summary>The region the user picked, in physical virtual-desktop pixels.</summary>
public sealed record SnipSelection(SnipMode Mode, PixelRect Region, IReadOnlyList<(double X, double Y)>? Outline);

/// <summary>
/// The frozen-screen overlay: one borderless topmost window per display showing what was captured,
/// dimmed outside the selection, with the snip mode bar on the display under the pointer.
/// </summary>
public sealed class OverlaySession
{
    private readonly ScreenSnapshot snapshot;
    private readonly List<OverlayWindow> windows = [];
    private readonly TaskCompletionSource<SnipSelection?> result = new();
    private PixelPoint? anchor;
    private PixelRect? selection;
    private List<(double X, double Y)>? outline;
    private bool finished;

    public OverlaySession(ScreenSnapshot snapshot, SnipMode mode)
    {
        this.snapshot = snapshot;
        Mode = mode;
    }

    public event EventHandler? Changed;

    public SnipMode Mode { get; private set; }

    public ScreenSnapshot Snapshot => snapshot;

    /// <summary>The rectangle being dragged or the hovered window or display.</summary>
    public PixelRect? Selection => selection;

    public IReadOnlyList<(double X, double Y)>? Outline => outline;

    public bool IsDragging => anchor is not null;

    public Task<SnipSelection?> ShowAsync(PixelPoint pointer)
    {
        MonitorCapture barMonitor = snapshot.MonitorAt(pointer.X, pointer.Y)
            ?? snapshot.Monitors.FirstOrDefault(m => m.IsPrimary) ?? snapshot.Monitors[0];
        foreach (MonitorCapture monitor in snapshot.Monitors)
        {
            var window = new OverlayWindow(this, monitor, showBar: monitor == barMonitor);
            windows.Add(window);
            window.Show();
        }

        windows.FirstOrDefault(w => w.Monitor == barMonitor)?.Activate();
        UpdateHover(pointer);
        return result.Task;
    }

    public void SetMode(SnipMode mode)
    {
        Mode = mode;
        anchor = null;
        selection = null;
        outline = null;
        Changed?.Invoke(this, EventArgs.Empty);

        // A whole window is picked on the live screen, so the frozen one goes away.
        if (mode == SnipMode.WholeWindow)
            Finish(null);
    }

    public void Cancel() => Finish(null);

    public void PointerPressed(PixelPoint point)
    {
        switch (Mode)
        {
            case SnipMode.Rectangle:
                anchor = point;
                selection = new PixelRect(point.X, point.Y, 0, 0);
                break;
            case SnipMode.Freeform:
                anchor = point;
                outline = [(point.X + 0.5, point.Y + 0.5)];
                selection = null;
                break;
            case SnipMode.Window:
            case SnipMode.Fullscreen:
                UpdateHover(point);
                break;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void PointerMoved(PixelPoint point)
    {
        if (anchor is not { } start)
        {
            UpdateHover(point);
            return;
        }

        if (Mode == SnipMode.Rectangle)
        {
            selection = PixelRect.FromEdges(Math.Min(start.X, point.X), Math.Min(start.Y, point.Y),
                Math.Max(start.X, point.X), Math.Max(start.Y, point.Y));
        }
        else if (Mode == SnipMode.Freeform && outline is not null)
        {
            (double lastX, double lastY) = outline[^1];
            if (Math.Abs(point.X + 0.5 - lastX) + Math.Abs(point.Y + 0.5 - lastY) >= 2)
                outline.Add((point.X + 0.5, point.Y + 0.5));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void PointerReleased(PixelPoint point)
    {
        switch (Mode)
        {
            case SnipMode.Rectangle when anchor is not null:
                PointerMoved(point);
                anchor = null;
                if (selection is { Width: >= 2, Height: >= 2 } rect)
                    Finish(new SnipSelection(Mode, rect, null));
                else
                    selection = null;
                break;
            case SnipMode.Freeform when anchor is not null && outline is not null:
                anchor = null;
                PixelRect bounds = OutlineBounds(outline);
                if (outline.Count >= 3 && bounds.Width >= 4 && bounds.Height >= 4)
                    Finish(new SnipSelection(Mode, bounds, outline));
                else
                    outline = null;
                break;
            case SnipMode.Window:
            case SnipMode.Fullscreen:
                UpdateHover(point);
                if (selection is { } hovered)
                    Finish(new SnipSelection(Mode, hovered, null));
                break;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateHover(PixelPoint point)
    {
        PixelRect? hovered = Mode switch
        {
            SnipMode.Window => snapshot.WindowAt(point.X, point.Y)?.Bounds ?? snapshot.MonitorAt(point.X, point.Y)?.Bounds,
            SnipMode.Fullscreen => snapshot.MonitorAt(point.X, point.Y)?.Bounds,
            _ => null,
        };
        if (hovered != selection)
        {
            selection = hovered;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Finish(SnipSelection? selected)
    {
        if (finished)
            return;
        finished = true;
        foreach (OverlayWindow window in windows)
            window.Close();
        windows.Clear();
        result.TrySetResult(selected);
    }

    private static PixelRect OutlineBounds(IReadOnlyList<(double X, double Y)> points)
    {
        double left = points.Min(p => p.X), top = points.Min(p => p.Y);
        double right = points.Max(p => p.X), bottom = points.Max(p => p.Y);
        return PixelRect.FromEdges((int)Math.Floor(left), (int)Math.Floor(top), (int)Math.Ceiling(right), (int)Math.Ceiling(bottom));
    }
}

/// <summary>A borderless window covering one display with its frozen image.</summary>
internal sealed class OverlayWindow : Window
{
    private readonly OverlaySession session;
    private readonly HdrImageView frozen;
    private readonly List<ToggleButton> modeButtons = [];

    public OverlayWindow(OverlaySession session, MonitorCapture monitor, bool showBar)
    {
        this.session = session;
        Monitor = monitor;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(monitor.Bounds.X, monitor.Bounds.Y);
        Width = monitor.Bounds.Width / monitor.Scaling;
        Height = monitor.Bounds.Height / monitor.Scaling;
        Title = "WSnip overlay";
        Cursor = new Cursor(StandardCursorType.Cross);

        frozen = new HdrImageView { Smooth = false, Image = SharedImage.FromLinear(monitor.Image, monitor.Color.WhiteScale) };
        var canvas = new OverlayCanvas(session, this);
        var root = new Grid();
        root.Children.Add(frozen);
        root.Children.Add(canvas);
        if (showBar)
            root.Children.Add(CreateBar());
        Content = root;

        session.Changed += OnSessionChanged;
        Closed += (_, _) =>
        {
            session.Changed -= OnSessionChanged;
            frozen.Image = null;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                session.Cancel();
        };
    }

    public MonitorCapture Monitor { get; }

    /// <summary>Converts a window position in device-independent pixels to virtual-desktop pixels.</summary>
    public PixelPoint ToPhysical(Point point)
    {
        double scale = RenderScaling;
        return new PixelPoint(Monitor.Bounds.X + (int)Math.Floor(point.X * scale), Monitor.Bounds.Y + (int)Math.Floor(point.Y * scale));
    }

    public Point ToLocal(double x, double y)
    {
        double scale = RenderScaling;
        return new Point((x - Monitor.Bounds.X) / scale, (y - Monitor.Bounds.Y) / scale);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // The window takes the scale of the display it opened on; fit it to the display exactly.
        Width = Monitor.Bounds.Width / RenderScaling;
        Height = Monitor.Bounds.Height / RenderScaling;
        Position = new PixelPoint(Monitor.Bounds.X, Monitor.Bounds.Y);
    }

    private Control CreateBar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (SnipModeOption option in SnipModeOption.All)
        {
            SnipMode mode = option.Mode;
            var button = new ToggleButton
            {
                Classes = { "tool" },
                Content = CreateIcon(option.IconKey),
                IsChecked = session.Mode == mode,
                Cursor = new Cursor(StandardCursorType.Arrow),
            };
            ToolTip.SetTip(button, $"{option.Label}: {option.Description}");
            AutomationProperties.SetName(button, option.Label);
            button.Click += (_, _) =>
            {
                session.SetMode(mode);
                foreach (ToggleButton other in modeButtons)
                    other.IsChecked = other == button;
            };
            modeButtons.Add(button);
            panel.Children.Add(button);
        }

        panel.Children.Add(new Border { Classes = { "divider" } });
        var close = new Button { Classes = { "icon" }, Content = CreateIcon("IconClose"), Cursor = new Cursor(StandardCursorType.Arrow) };
        ToolTip.SetTip(close, "Cancel (Esc)");
        close.Click += (_, _) => session.Cancel();
        panel.Children.Add(close);

        return new Border
        {
            Classes = { "overlayBar" },
            Child = panel,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
        };
    }

    private static Control CreateIcon(string key) =>
        new Viewbox { Width = 18, Height = 18, Child = new ShapePath { Data = Application.Current?.FindResource(key) as Geometry } };

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        foreach (Control child in ((Grid)Content!).Children)
            child.InvalidateVisual();
    }
}

/// <summary>Dims everything but the selection and turns pointer input into selection updates.</summary>
/// <remarks>The selection is left undrawn, so the control claims hits itself rather than relying on drawn content.</remarks>
internal sealed class OverlayCanvas : Control, Avalonia.Rendering.ICustomHitTest
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0));
    private static readonly IPen Outline = new Pen(Brushes.White, 1.5);
    private static readonly IPen Shadow = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 3.5);
    private readonly OverlaySession session;
    private readonly OverlayWindow window;

    public OverlayCanvas(OverlaySession session, OverlayWindow window)
    {
        this.session = session;
        this.window = window;
        Focusable = true;
    }

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        Geometry? hole = SelectionGeometry();
        if (hole is null)
        {
            context.FillRectangle(Dim, bounds);
            return;
        }

        var dimmed = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dimmed.Children.Add(new RectangleGeometry(bounds));
        dimmed.Children.Add(hole);
        context.DrawGeometry(Dim, null, dimmed);
        context.DrawGeometry(null, Shadow, hole);
        context.DrawGeometry(null, Outline, hole);

        if (session.Selection is { } rect && session.Mode != SnipMode.Freeform)
            DrawSize(context, rect);
    }

    private Geometry? SelectionGeometry()
    {
        if (session.Mode == SnipMode.Freeform && session.Outline is { Count: >= 2 } points)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext stream = geometry.Open())
            {
                stream.BeginFigure(window.ToLocal(points[0].X, points[0].Y), true);
                for (int i = 1; i < points.Count; i++)
                    stream.LineTo(window.ToLocal(points[i].X, points[i].Y));
                stream.EndFigure(true);
            }

            return geometry;
        }

        if (session.Selection is not { } rect || rect.Width <= 0 || rect.Height <= 0)
            return null;
        Point topLeft = window.ToLocal(rect.X, rect.Y);
        Point bottomRight = window.ToLocal(rect.Right, rect.Bottom);
        return new RectangleGeometry(new Rect(topLeft, bottomRight));
    }

    private void DrawSize(DrawingContext context, PixelRect rect)
    {
        Point topLeft = window.ToLocal(rect.X, rect.Y);
        if (topLeft.X > Bounds.Width || topLeft.Y > Bounds.Height || window.ToLocal(rect.Right, rect.Bottom) is { X: < 0 } or { Y: < 0 })
            return;
        var text = new FormattedText($"{rect.Width} \u00D7 {rect.Height}", System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Inter"), 12, Brushes.White);
        double x = Math.Clamp(topLeft.X, 4, Math.Max(4, Bounds.Width - text.Width - 16));
        double y = topLeft.Y - text.Height - 12 >= 0 ? topLeft.Y - text.Height - 10 : topLeft.Y + 8;
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(0xC0, 0x20, 0x20, 0x20)), new Rect(x, y, text.Width + 12, text.Height + 4), 4);
        context.DrawText(text, new Point(x + 6, y + 2));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPoint point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            session.Cancel();
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;
        e.Pointer.Capture(this);
        session.PointerPressed(window.ToPhysical(point.Position));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        session.PointerMoved(window.ToPhysical(e.GetPosition(this)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        e.Pointer.Capture(null);
        session.PointerReleased(window.ToPhysical(e.GetPosition(this)));
        e.Handled = true;
    }
}
