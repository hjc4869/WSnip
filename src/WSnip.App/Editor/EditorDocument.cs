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
    private readonly CancellationTokenSource previewCancellation = new();
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
        history.Add(new EditorState(snip.Image.Share(), snip.Statistics, []));
    }

    public event EventHandler? Changed;

    /// <summary>Raised when a previewed SDR rendition is ready to be shown.</summary>
    public event EventHandler? RenditionChanged;

    public DateTimeOffset CapturedAt { get; }

    public double SourceSdrWhiteNits { get; }

    public double? SourcePeakNits { get; }

    public EditorState Current
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return history[position];
        }
    }

    public int Width => Current.Image.Width;

    public int Height => Current.Image.Height;

    public bool CanUndo => !disposed && position > 0;

    public bool CanRedo => !disposed && position < history.Count - 1;

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
        using HdrImage cropped = Current.Image.View(region);
        var delta = new System.Numerics.Vector2(-region.X, -region.Y);
        HdrStatistics statistics = HdrStatistics.Measure(cropped);
        AnnotationStroke[] strokes = Current.Strokes.Select(s => s.Offset(delta)).ToArray();
        Push(new EditorState(cropped.Share(), statistics, strokes));
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
    public async Task<Snip> FlattenAsync()
    {
        // Acquire on the UI thread, before queuing any work. Closing/replacing the document
        // immediately disposes history, while this operation retains only the image it needs.
        EditorState state = Current;
        using HdrImage source = state.Image.Share();
        ToneMapSettings? toneMap = ToneMap;
        return await Task.Run(() =>
        {
            using HdrImage image = AnnotationRenderer.Render(source, state.Strokes);
            return new Snip(image.Share(), CapturedAt, SourceSdrWhiteNits, SourcePeakNits,
                state.Strokes.Count == 0 ? state.Statistics : null) { ToneMap = toneMap };
        });
    }

    /// <summary>The image as shown on an SDR surface: the same rendition an SDR file would get.</summary>
    /// <param name="defaults">Tone mapping unless the snip was tuned or a preview is showing.</param>
    public SharedImage GetSdrImage(ToneMapSettings defaults)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // Opening tone mapping from HDR already started a background build. Keep the previous
        // frame until it arrives, rather than allocating/processing a second rendition on the UI thread.
        if (sdrImage is null && buildingPreview && PreviewToneMap is not null && hdrImage is not null)
            return hdrImage;
        hdrImage?.Release();
        hdrImage = null;
        ToneMapSettings toneMap = EffectiveToneMap(defaults);

        // While a preview builds in the background, the previous rendition stays on screen.
        if (sdrImage is not null && (toneMap.Equals(sdrToneMap) || (buildingPreview && PreviewToneMap is not null)))
            return sdrImage;

        using Rendition rendition = BuildSdr(Current, toneMap, previewCancellation.Token);
        SharedImage next = SharedImage.FromSdr(rendition);
        sdrImage?.Release();
        sdrImage = next;
        sdrToneMap = toneMap;
        return sdrImage;
    }

    /// <summary>The relative image for an extended-range surface, rolled off to fit the given headroom.</summary>
    public SharedImage GetHdrImage(float headroom)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        sdrImage?.Release();
        sdrImage = null;
        headroom = MathF.Max(1, headroom);
        EditorState state = Current;
        float peak = state.Statistics.PeakComponent;

        // Without HDR content, values above one are wide-gamut colors that need no roll-off.
        float effective = !state.Statistics.HasHdr || peak <= headroom * 1.02f ? float.PositiveInfinity : headroom;
        if (hdrImage is null || effective != hdrHeadroom)
        {
            using HdrImage fitted = float.IsPositiveInfinity(effective) ? state.Image.Share() : DisplayRendition.FitToHeadroom(state.Image, peak, headroom, (float)SourceSdrWhiteNits);
            SharedImage next = SharedImage.FromHdr(fitted);
            hdrImage?.Release();
            hdrImage = next;
            hdrHeadroom = effective;
        }

        return hdrImage;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        previewCancellation.Cancel();
        pendingPreview = null;
        InvalidateImages();
        foreach (HdrImage image in history.Select(state => state.Image).Distinct())
            image.Dispose();
        history.Clear();
        position = 0;
        Changed = null;
        RenditionChanged = null;
        if (!buildingPreview)
            previewCancellation.Dispose();
    }

    private Rendition BuildSdr(EditorState state, ToneMapSettings toneMap, CancellationToken cancellationToken) =>
        RenditionBuilder.Build(state.Image, state.Statistics, new RenditionOptions
        {
            ToneMap = toneMap,
            SdrWhiteNits = SourceSdrWhiteNits,
            BuildGainMap = false,
            BasePrimaries = ColorPrimaries.Bt709,
        }, cancellationToken);

    private async Task BuildPreviewsAsync()
    {
        buildingPreview = true;
        try
        {
            while (pendingPreview is { } toneMap && !disposed)
            {
                pendingPreview = null;
                EditorState state = Current;
                using HdrImage source = state.Image.Share();
                CancellationToken cancellationToken = previewCancellation.Token;
                using Rendition rendition = await Task.Run(() => BuildSdr(state with { Image = source }, toneMap, cancellationToken), cancellationToken);
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
        catch (OperationCanceledException) when (disposed)
        {
            // Closing a snip cancels row processing and releases the worker's last image lease.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LightStudio.Logging.AppLog.Error("Editor", "Previewing the tone mapping failed.", exception);
        }
        finally
        {
            buildingPreview = false;
            if (disposed)
                previewCancellation.Dispose();
        }
    }

    private void Push(EditorState state)
    {
        bool imageChanged = !ReferenceEquals(state.Image, Current.Image);
        HdrImage[] abandoned = history.Skip(position + 1).Select(entry => entry.Image).Distinct().ToArray();
        history.RemoveRange(position + 1, history.Count - position - 1);
        history.Add(state);
        foreach (HdrImage image in abandoned)
        {
            if (!history.Any(entry => ReferenceEquals(entry.Image, image)))
                image.Dispose();
        }
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
