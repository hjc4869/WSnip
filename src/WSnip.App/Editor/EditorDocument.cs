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
    private ToneMapSettings? sdrToneMap;
    private ToneMapSettings? pendingPreview;
    private bool buildingPreview;
    private bool disposed;

    public EditorDocument(Snip snip)
    {
        CapturedAt = snip.CapturedAt;
        SourceSdrWhiteNits = snip.SourceSdrWhiteNits;
        SourcePeakNits = snip.SourceDisplayPeakNits;
        ToneMap = snip.ToneMap;
        history.Add(new EditorState(snip.Image, snip.Statistics, []));
    }

    public event EventHandler? Changed;

    /// <summary>Raised when a previewed SDR rendition is ready to be shown.</summary>
    public event EventHandler? RenditionChanged;

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

    /// <summary>Tone mapping tuned for this snip; null follows the settings' default.</summary>
    public ToneMapSettings? ToneMap { get; private set; }

    /// <summary>Tone mapping being tried out, shown instead of <see cref="ToneMap"/> until it is committed or dropped.</summary>
    public ToneMapSettings? PreviewToneMap { get; private set; }

    /// <summary>The tone mapping the SDR rendition is shown with.</summary>
    public ToneMapSettings EffectiveToneMap(ToneMapSettings defaults) => PreviewToneMap ?? ToneMap ?? defaults;

    public void SetToneMap(ToneMapSettings? toneMap)
    {
        if (Equals(toneMap, ToneMap))
            return;
        ToneMap = toneMap;
        IsDirty = true;
        OnChanged(imageMayChange: false);
    }

    /// <summary>
    /// Shows tone mapping without committing it, or stops previewing when null. The rendition is
    /// built in the background, the newest request winning, and <see cref="RenditionChanged"/>
    /// is raised when it can be shown; until then the previous rendition stays on screen.
    /// </summary>
    public void Preview(ToneMapSettings? toneMap)
    {
        if (disposed)
            return;
        PreviewToneMap = toneMap;
        if (toneMap is null || (sdrImage is not null && toneMap.Equals(sdrToneMap)))
        {
            pendingPreview = null;
            RenditionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        pendingPreview = toneMap;
        if (!buildingPreview)
            _ = BuildPreviewsAsync();
    }

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
        return new Snip(image, CapturedAt, SourceSdrWhiteNits, SourcePeakNits) { ToneMap = ToneMap };
    }

    /// <summary>The image as shown on an SDR surface: the same rendition an SDR file would get.</summary>
    /// <param name="defaults">Tone mapping unless the snip was tuned or a preview is showing.</param>
    public SharedImage GetSdrImage(ToneMapSettings defaults)
    {
        ToneMapSettings toneMap = EffectiveToneMap(defaults);

        // While a preview builds in the background, the previous rendition stays on screen.
        if (sdrImage is not null && (toneMap.Equals(sdrToneMap) || (buildingPreview && PreviewToneMap is not null)))
            return sdrImage;

        sdrImage?.Release();
        sdrImage = SharedImage.FromSdr(BuildSdr(Current, toneMap));
        sdrToneMap = toneMap;
        return sdrImage;
    }

    /// <summary>The relative image for an extended-range surface, rolled off to fit the given headroom.</summary>
    public SharedImage GetHdrImage(float headroom)
    {
        headroom = MathF.Max(1, headroom);
        EditorState state = Current;
        float peak = state.Statistics.PeakComponent;

        // Without HDR content, values above one are wide-gamut colors that need no roll-off.
        float effective = !state.Statistics.HasHdr || peak <= headroom * 1.02f ? float.PositiveInfinity : headroom;
        if (hdrImage is null || effective != hdrHeadroom)
        {
            hdrImage?.Release();
            HdrImage fitted = float.IsPositiveInfinity(effective) ? state.Image : DisplayRendition.FitToHeadroom(state.Image, peak, headroom, (float)SourceSdrWhiteNits);
            hdrImage = SharedImage.FromHdr(fitted);
            hdrHeadroom = effective;
        }

        return hdrImage;
    }

    public void Dispose()
    {
        disposed = true;
        pendingPreview = null;
        InvalidateImages();
    }

    private Rendition BuildSdr(EditorState state, ToneMapSettings toneMap) =>
        RenditionBuilder.Build(state.Image, state.Statistics, new RenditionOptions
        {
            ToneMap = toneMap,
            SdrWhiteNits = SourceSdrWhiteNits,
            BuildGainMap = false,
            BasePrimaries = ColorPrimaries.Bt709,
        });

    private async Task BuildPreviewsAsync()
    {
        buildingPreview = true;
        try
        {
            while (pendingPreview is { } toneMap && !disposed)
            {
                pendingPreview = null;
                EditorState state = Current;
                Rendition rendition = await Task.Run(() => BuildSdr(state, toneMap));
                if (disposed || PreviewToneMap is not { } wanted)
                    return;

                // A crop or undo meanwhile replaced the image; build the preview for the new one.
                if (!ReferenceEquals(state.Image, Current.Image))
                {
                    pendingPreview ??= wanted;
                    continue;
                }

                // Every finished build is shown, even with a newer one pending, so that dragging a
                // slider updates continuously; one that already shows the wanted settings stays.
                if (!(sdrImage is not null && wanted.Equals(sdrToneMap)))
                {
                    sdrImage?.Release();
                    sdrImage = SharedImage.FromSdr(rendition);
                    sdrToneMap = toneMap;
                }

                if (pendingPreview is null && !wanted.Equals(sdrToneMap))
                    pendingPreview = wanted;
                if (pendingPreview is null)
                    buildingPreview = false;
                RenditionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LightStudio.Logging.AppLog.Error("Editor", "Previewing the tone mapping failed.", exception);
        }
        finally
        {
            buildingPreview = false;
        }
    }

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
