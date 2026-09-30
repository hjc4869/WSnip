using LightStudio.Logging;
using Tmds.DBus.Protocol;
using WSnip.Core.Capture;
using WSnip.Core.Platform;
using WSnip.Linux.Portal;

namespace WSnip.Linux.Capture;

/// <summary>
/// Picks a window with KWin's own pointer where KWin streams windows to WSnip: KWin's interactive
/// selection, over its D-Bus interface, keeps the click from the window and cancels on Esc or a
/// right click. Otherwise the portal's sharing dialog picks the window when the capture starts.
/// </summary>
public sealed class KWinWindowPicker : IWindowPickerService
{
    private const string Service = "org.kde.KWin";
    private const string Cancelled = "org.kde.KWin.Error.UserCancel";
    private const string NotAWindow = "org.kde.KWin.Error.InvalidWindow";
    private const string AccessDenied = "org.freedesktop.DBus.Error.AccessDenied";
    private const int DesktopWindowType = 1;

    private readonly SessionBus bus;
    private readonly KWinScreenCaptureService capture;
    private readonly PortalWindowPicker portal;

    internal KWinWindowPicker(SessionBus bus, KWinScreenCaptureService capture, PortalWindowPicker portal)
    {
        this.bus = bus;
        this.capture = capture;
        this.portal = portal;
    }

    public bool IsSupported => portal.IsSupported;

    public async Task<CapturedWindow?> PickAsync(CancellationToken cancellationToken = default)
    {
        if (!await capture.IsKWinAvailableAsync().ConfigureAwait(false))
            return await portal.PickAsync(cancellationToken).ConfigureAwait(false);

        DBusConnection connection = await bus.ConnectAsync().ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, VariantValue> info;
            try
            {
                info = await QueryWindowInfoAsync(connection).ConfigureAwait(false);
            }
            catch (DBusErrorReplyException exception) when (exception.ErrorName == Cancelled)
            {
                return null;
            }
            catch (DBusErrorReplyException exception) when (exception.ErrorName == NotAWindow)
            {
                AppLog.Information("Picker", "There is no window to capture there; still picking.");
                continue;
            }
            catch (DBusErrorReplyException exception) when (exception.ErrorName == AccessDenied || DesktopPortal.IsMissing(exception))
            {
                AppLog.Information("Picker", $"KWin does not pick windows for WSnip ({exception.ErrorName}: {exception.ErrorMessage}); the sharing dialog picks one.");
                return await portal.PickAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Value(info, "type") is { Type: VariantValueType.Int32 } type && type.GetInt32() == DesktopWindowType)
            {
                AppLog.Information("Picker", "The desktop is not a window to capture; still picking.");
                continue;
            }

            string uuid = Text(info, "uuid") ?? throw new InvalidOperationException("KWin picked a window without an id.");
            return new CapturedWindow(capture.AddPickedWindow(uuid), Text(info, "caption") ?? string.Empty,
                Text(info, "resourceClass") is { Length: > 0 } application ? application : null, default);
        }
    }

    /// <summary>Has KWin show its selection pointer and answer with the window clicked.</summary>
    private static Task<Dictionary<string, VariantValue>> QueryWindowInfoAsync(DBusConnection connection)
    {
        MessageWriter writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: "/KWin", @interface: Service, member: "queryWindowInfo");
        return connection.CallMethodAsync(writer.CreateMessage(),
            static (message, _) => message.GetBodyReader().ReadDictionaryOfStringToVariantValue(), null);
    }

    private static VariantValue? Value(Dictionary<string, VariantValue> info, string key) =>
        info.TryGetValue(key, out VariantValue value) ? DesktopPortal.Unwrap(value) : (VariantValue?)null;

    private static string? Text(Dictionary<string, VariantValue> info, string key) =>
        Value(info, key) is { Type: VariantValueType.String } text ? text.GetString() : null;
}
