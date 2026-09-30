using WSnip.App.Rendering;
using WSnip.Core.Capture;
using WSnip.Core.Editing;
using WSnip.Core.Encoding;
using WSnip.Core.Imaging;

namespace WSnip.App.Editor;

/// <summary>One state of the editor: the (possibly cropped) image and the strokes drawn over it.</summary>
public sealed record EditorState(HdrImage Image, HdrStatistics Statistics, IReadOnlyList<AnnotationStroke> Strokes);

/// <summary>A snip being edited, with undo history and cached display images.</summary>
public sealed class EditorDocument : IDisposable
{
    private readonly List<EditorState> history = [];
    private int position;
    private SharedImage? sdrImage;
    private SharedImage? hdrImage;
    private float hdrHeadroom;
    private SdrToneMapping sdrMapping;

    public EditorDocument(Snip snip)
    {
        CapturedAt = snip.CapturedAt;
        SourceSdrWhiteNits = snip.SourceSdrWhiteNits;
        SourcePeakNits = snip.SourceDisplayPeakNits;
        history.Add(new EditorState(snip.Image, snip.Statistics, []));
    }

    public event EventHandler? Changed;

    public DateTimeOffset CapturedAt { get; }

    public double SourceSdrWhiteNits { get; }

    public double? SourcePeakNits { get; }

    public EditorState Current => history[position];

    public int Width => Current.Image.Width;

    public int Height => Current.Image.Height;

    public bool CanUndo => position > 0;

    public bool CanRedo => position < history.Count - 1;

    /// <summary>Whether the document changed since it was last saved or copied.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>A pixel of the current image: relative scRGB and straight alpha.</summary>
    public (ScRgb Color, float Alpha) PixelAt(int x, int y)
    {
        ReadOnlySpan<Half> row = Current.Image.ReadRow(Math.Clamp(y, 0, Height - 1));
        int i = Math.Clamp(x, 0, Width - 1) * HdrImage.Channels;
        return (new ScRgb((float)row[i], (float)row[i + 1], (float)row[i + 2]), (float)row[i + 3]);
    }

    public void AddStroke(AnnotationStroke stroke) =>
        Push(Current with { Strokes = [.. Current.Strokes, stroke] });

    public void Erase(IReadOnlyCollection<AnnotationStroke> strokes)
    {
        if (strokes.Count > 0)
            Push(Current with { Strokes = Current.Strokes.Where(s => !strokes.Contains(s)).ToArray() });
    }

    public void Crop(PixelRect region)
    {
        region = region.Intersect(new PixelRect(0, 0, Width, Height));
        if (region.IsEmpty || region == new PixelRect(0, 0, Width, Height))
            return;
        HdrImage cropped = Current.Image.Crop(region);
        var delta = new System.Numerics.Vector2(-region.X, -region.Y);
        Push(new EditorState(cropped, HdrStatistics.Measure(cropped), Current.Strokes.Select(s => s.Offset(delta)).ToArray()));
    }

    public void Undo()
    {
        if (!CanUndo)
            return;
        HdrImage before = Current.Image;
        position--;
        IsDirty = true;
        OnChanged(!ReferenceEquals(before, Current.Image));
    }

    public void Redo()
    {
        if (!CanRedo)
            return;
        HdrImage before = Current.Image;
        position++;
        IsDirty = true;
        OnChanged(!ReferenceEquals(before, Current.Image));
    }

    public void MarkClean() => IsDirty = false;

    /// <summary>The snip with its annotations burnt in, ready to export.</summary>
    public Snip Flatten()
    {
        EditorState state = Current;
        HdrImage image = AnnotationRenderer.Render(state.Image, state.Strokes);
        return new Snip(image, CapturedAt, SourceSdrWhiteNits, SourcePeakNits);
    }

    /// <summary>The image as shown on an SDR surface: the same rendition an SDR file would get.</summary>
    public SharedImage GetSdrImage(SdrToneMapping mapping)
    {
        if (sdrImage is null || sdrMapping != mapping)
        {
            sdrImage?.Release();
            EditorState state = Current;
            Rendition rendition = RenditionBuilder.Build(state.Image, state.Statistics, new RenditionOptions
            {
                ToneMapping = mapping,
                BuildGainMap = false,
                BasePrimaries = ColorPrimaries.Bt709,
            });
            sdrImage = SharedImage.FromSdr(rendition);
            sdrMapping = mapping;
        }

        return sdrImage;
    }

    /// <summary>The relative image for an extended-range surface, rolled off to fit the given headroom.</summary>
    public SharedImage GetHdrImage(float headroom)
    {
        headroom = MathF.Max(1, headroom);
        EditorState state = Current;
        float peak = state.Statistics.PeakComponent;
        float effective = peak <= headroom * 1.02f ? float.PositiveInfinity : headroom;
        if (hdrImage is null || effective != hdrHeadroom)
        {
            hdrImage?.Release();
            HdrImage fitted = float.IsPositiveInfinity(effective) ? state.Image : DisplayRendition.FitToHeadroom(state.Image, peak, headroom);
            hdrImage = SharedImage.FromHdr(fitted);
            hdrHeadroom = effective;
        }

        return hdrImage;
    }

    public void Dispose() => InvalidateImages();

    private void Push(EditorState state)
    {
        bool imageChanged = !ReferenceEquals(state.Image, Current.Image);
        history.RemoveRange(position + 1, history.Count - position - 1);
        history.Add(state);
        position = history.Count - 1;
        IsDirty = true;
        OnChanged(imageChanged);
    }

    private void OnChanged(bool imageMayChange)
    {
        if (imageMayChange)
            InvalidateImages();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void InvalidateImages()
    {
        sdrImage?.Release();
        sdrImage = null;
        hdrImage?.Release();
        hdrImage = null;
    }
}
