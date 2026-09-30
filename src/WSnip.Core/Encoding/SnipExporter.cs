using System.Globalization;
using WSnip.Core.Capture;
using WSnip.Core.Imaging;

namespace WSnip.Core.Encoding;

public enum SnipFormat
{
    /// <summary>8-bit PNG of the SDR rendition.</summary>
    Png,

    /// <summary>JPEG with an Ultra HDR / ISO 21496-1 gain map when the snip holds HDR content.</summary>
    Jpeg,

    /// <summary>HEIC with an ISO 21496-1 gain map when the snip holds HDR content.</summary>
    Heic,

    /// <summary>AVIF with an ISO 21496-1 gain map when the snip holds HDR content.</summary>
    Avif,

    /// <summary>JPEG XL: 16-bit BT.2100 PQ when the snip holds HDR content, 8-bit otherwise.</summary>
    JpegXl,

    /// <summary>16-bit PNG in BT.2100 PQ, tagged with cICP.</summary>
    PngHdr,
}

public sealed record ExportOptions
{
    public int Quality { get; init; } = 90;

    public int GainMapQuality { get; init; } = 90;

    public SdrToneMapping ToneMapping { get; init; } = SdrToneMapping.Adaptive;

    /// <summary>Whether HDR content is kept through a gain map; otherwise only the SDR rendition is written.</summary>
    public bool PreserveHdr { get; init; } = true;

    /// <summary>JPEG XL encoder effort, 1 (fastest) to 9.</summary>
    public int JpegXlEffort { get; init; } = 7;
}

/// <summary>Encodes snips into the supported file formats.</summary>
public static class SnipExporter
{
    public static IReadOnlyList<SnipFormat> AllFormats { get; } = Enum.GetValues<SnipFormat>();

    public static string Extension(SnipFormat format) => format switch
    {
        SnipFormat.Jpeg => ".jpg",
        SnipFormat.Heic => ".heic",
        SnipFormat.Avif => ".avif",
        SnipFormat.JpegXl => ".jxl",
        _ => ".png",
    };

    public static string DisplayName(SnipFormat format) => format switch
    {
        SnipFormat.Png => "PNG",
        SnipFormat.PngHdr => "PNG HDR (16-bit PQ)",
        SnipFormat.Jpeg => "JPEG (Ultra HDR)",
        SnipFormat.Heic => "HEIC (gain map)",
        SnipFormat.Avif => "AVIF (gain map)",
        SnipFormat.JpegXl => "JPEG XL",
        _ => format.ToString(),
    };

    public static string MimeType(SnipFormat format) => format switch
    {
        SnipFormat.Jpeg => "image/jpeg",
        SnipFormat.Heic => "image/heic",
        SnipFormat.Avif => "image/avif",
        SnipFormat.JpegXl => "image/jxl",
        _ => "image/png",
    };

    public static bool IsAvailable(SnipFormat format) => format switch
    {
        SnipFormat.Heic => HeifEncoder.IsAvailable(HeifCodec.Hevc),
        SnipFormat.Avif => HeifEncoder.IsAvailable(HeifCodec.Av1),
        SnipFormat.JpegXl => JxlEncoder.IsAvailable(),
        _ => true,
    };

    public static SnipFormat? FromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => SnipFormat.Png,
        ".jpg" or ".jpeg" => SnipFormat.Jpeg,
        ".heic" or ".heif" => SnipFormat.Heic,
        ".avif" => SnipFormat.Avif,
        ".jxl" => SnipFormat.JpegXl,
        _ => null,
    };

    /// <summary>A file name in the style of the Windows Snipping Tool.</summary>
    public static string DefaultFileName(DateTimeOffset capturedAt, SnipFormat format) =>
        "Screenshot " + capturedAt.ToLocalTime().ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) + Extension(format);

    /// <summary>The SDR rendition used for display in SDR, the clipboard and SDR-only formats.</summary>
    public static Rendition BuildSdr(Snip snip, SdrToneMapping toneMapping, bool flattenAlpha) =>
        RenditionBuilder.Build(snip.Image, snip.Statistics, new RenditionOptions
        {
            ToneMapping = toneMapping,
            FlattenAlpha = flattenAlpha,
            BuildGainMap = false,
            BasePrimaries = ColorPrimaries.Bt709,
        });

    public static void Export(Stream output, Snip snip, SnipFormat format, ExportOptions options)
    {
        bool hdr = options.PreserveHdr && snip.Statistics.HasHdr;
        switch (format)
        {
            case SnipFormat.Png:
                PngWriter.WriteSdr(output, Build(snip, options, flatten: false, gainMap: false));
                break;
            case SnipFormat.PngHdr:
                PngWriter.WriteHdr(output, PqImageBuilder.Build(snip.Image, flattenAlpha: false));
                break;
            case SnipFormat.Jpeg:
                UltraHdrJpegWriter.Write(output, Build(snip, options, flatten: true, gainMap: hdr), options.Quality, options.GainMapQuality);
                break;
            case SnipFormat.Heic:
                HeifEncoder.Write(output, Build(snip, options, flatten: true, gainMap: hdr), HeifCodec.Hevc, options.Quality, options.GainMapQuality);
                break;
            case SnipFormat.Avif:
                HeifEncoder.Write(output, Build(snip, options, flatten: true, gainMap: hdr), HeifCodec.Av1, options.Quality, options.GainMapQuality);
                break;
            case SnipFormat.JpegXl:
                if (hdr)
                    JxlEncoder.WriteHdr(output, PqImageBuilder.Build(snip.Image, flattenAlpha: false), options.Quality, options.JpegXlEffort);
                else
                    JxlEncoder.WriteSdr(output, Build(snip, options, flatten: false, gainMap: false), options.Quality, options.JpegXlEffort);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }
    }

    public static async Task ExportFileAsync(string path, Snip snip, SnipFormat format, ExportOptions options, CancellationToken cancellationToken = default)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new ArgumentException("Invalid path.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            await Task.Run(() =>
            {
                using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                Export(stream, snip, format, options);
            }, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    private static Rendition Build(Snip snip, ExportOptions options, bool flatten, bool gainMap) =>
        RenditionBuilder.Build(snip.Image, snip.Statistics, new RenditionOptions
        {
            ToneMapping = options.ToneMapping,
            FlattenAlpha = flatten,
            BuildGainMap = gainMap,
        });
}
