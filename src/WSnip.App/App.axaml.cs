using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WSnip.App.Services;
using WSnip.Core.Platform;
using WSnip.Core.Settings;

namespace WSnip.App;

public partial class App : Application
{
    /// <summary>Platform services, supplied by the application head before Avalonia starts.</summary>
    public static PlatformServices? Platform { get; set; }

    /// <summary>Command-line arguments of this launch.</summary>
    public static IReadOnlyList<string> Arguments { get; set; } = [];

    public static AppController? Controller { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            PlatformServices platform = Platform ?? throw new InvalidOperationException("Platform services were not configured.");
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Controller = new AppController(this, platform, new SettingsStore(Path.Combine(platform.DataDirectory, "settings.json")), desktop);
            Controller.Start(Arguments);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
