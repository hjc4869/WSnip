using System.Text.Json;
using System.Text.Json.Serialization;
using WSnip.Core.Capture;
using WSnip.Core.Encoding;
using WSnip.Core.Imaging;

namespace WSnip.Core.Settings;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public sealed class AppSettings
{
    /// <summary>The capture hotkey, such as "Win+Shift+A"; empty disables it.</summary>
    public string? Hotkey { get; set; }

    public SnipMode Mode { get; set; } = SnipMode.Rectangle;

    /// <summary>Seconds to wait before freezing the screen.</summary>
    public int DelaySeconds { get; set; }

    public bool IncludeCursor { get; set; }

    public bool CopyToClipboard { get; set; } = true;

    public bool AutoSave { get; set; } = true;

    /// <summary>Folder for automatically saved snips; null uses the system screenshot folder.</summary>
    public string? SaveFolder { get; set; }

    /// <summary>Format for snips without HDR content.</summary>
    public SnipFormat SdrFormat { get; set; } = SnipFormat.Png;

    /// <summary>Format for snips with HDR content.</summary>
    public SnipFormat HdrFormat { get; set; } = SnipFormat.Avif;

    public int Quality { get; set; } = 90;

    public SdrToneMapping ToneMapping { get; set; } = SdrToneMapping.Adaptive;

    public bool OpenEditorAfterCapture { get; set; } = true;

    public ThemePreference Theme { get; set; } = ThemePreference.System;

    public bool UseMica { get; set; } = true;

    public bool LaunchAtStartup { get; set; }

    public bool ShowHdrInEditor { get; set; } = true;

    /// <summary>Shows position, color and luminance of the pixel under the pointer in the editor.</summary>
    public bool ShowPixelInfo { get; set; } = true;

    /// <summary>Pen colors the user added, which may be wide-gamut or brighter than SDR white.</summary>
    public CustomColor[] CustomPenColors { get; set; } = [];

    /// <summary>Highlighter colors the user added; highlighters apply them within SDR.</summary>
    public CustomColor[] CustomHighlighterColors { get; set; } = [];

    /// <summary>Clones the settings. Collections are replaced rather than changed, so they may be shared.</summary>
    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>A copy with one change applied.</summary>
    public AppSettings With(Action<AppSettings> change)
    {
        AppSettings copy = Clone();
        change(copy);
        return copy;
    }

    public SnipFormat FormatFor(Snip snip) => snip.Statistics.HasHdr ? HdrFormat : SdrFormat;

    public ExportOptions ExportOptions() => new()
    {
        Quality = Quality,
        GainMapQuality = Quality,
        ToneMapping = ToneMapping,
    };
}

/// <summary>Loads and saves settings as JSON in the platform's data folder.</summary>
public sealed class SettingsStore(string path)
{
    public string Path { get; } = path;

    public AppSettings Load()
    {
        if (!File.Exists(Path))
            return new AppSettings();
        try
        {
            using FileStream stream = File.OpenRead(Path);
            AppSettings settings = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            settings.CustomPenColors ??= [];
            settings.CustomHighlighterColors ??= [];
            return settings;
        }
        catch (JsonException exception)
        {
            LightStudio.Logging.AppLog.Warning("Settings", $"Ignoring unreadable settings at {Path}.", exception);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temporary = Path + ".tmp";
        using (FileStream stream = File.Create(temporary))
            JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);
        File.Move(temporary, Path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
