using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace WSnip.Linux.Portal;

/// <summary>Kinds of content the ScreenCast portal shares.</summary>
[Flags]
internal enum ScreenCastSources : uint
{
    Monitor = 1,
    Window = 2,
    Virtual = 4,
}

/// <summary>One PipeWire stream of a screen cast.</summary>
/// <param name="Id">An identifier that stays the same when a selection is restored, when the portal gives one.</param>
/// <param name="Position">Top left in the compositor's logical coordinates, given for monitors.</param>
/// <param name="Size">Size in the compositor's logical coordinates.</param>
internal sealed record ScreenCastStream(uint NodeId, string? Id, ScreenCastSources SourceType, (int X, int Y)? Position, (int Width, int Height)? Size);

internal sealed record ScreenCastRequest
{
    public required ScreenCastSources Sources { get; init; }

    public bool Multiple { get; init; }

    public bool IncludeCursor { get; init; }

    /// <summary>Asks for a token that restores this selection next time without asking the user.</summary>
    public bool Persist { get; init; }

    public string? RestoreToken { get; init; }
}

/// <summary>What the ScreenCast portal of this session offers.</summary>
internal sealed record ScreenCastCapabilities(uint Version, ScreenCastSources Sources, uint CursorModes);

/// <summary>A started ScreenCast portal session, which shares its streams until it is disposed.</summary>
internal sealed class ScreenCastSession : IAsyncDisposable
{
    public const string Interface = "org.freedesktop.portal.ScreenCast";
    private const uint CursorHidden = 1, CursorEmbedded = 2;
    private const uint PersistUntilRevoked = 2;

    private readonly DBusConnection connection;
    private readonly string handle;
    private int closed;

    private ScreenCastSession(DBusConnection connection, string handle, IReadOnlyList<ScreenCastStream> streams, string? restoreToken)
    {
        this.connection = connection;
        this.handle = handle;
        Streams = streams;
        RestoreToken = restoreToken;
    }

    public IReadOnlyList<ScreenCastStream> Streams { get; }

    /// <summary>Restores the same selection in a later session, when the user allowed it.</summary>
    public string? RestoreToken { get; }

    /// <summary>Reads what the portal offers; fails with a D-Bus error when the portal or its ScreenCast interface is missing.</summary>
    public static async Task<ScreenCastCapabilities> QueryAsync(DBusConnection connection)
    {
        uint version = (await DesktopPortal.GetPropertyAsync(connection, Interface, "version").ConfigureAwait(false)).GetUInt32();
        var sources = (ScreenCastSources)(await DesktopPortal.GetPropertyAsync(connection, Interface, "AvailableSourceTypes").ConfigureAwait(false)).GetUInt32();
        uint cursorModes = version >= 2
            ? (await DesktopPortal.GetPropertyAsync(connection, Interface, "AvailableCursorModes").ConfigureAwait(false)).GetUInt32()
            : 0;
        return new ScreenCastCapabilities(version, sources, cursorModes);
    }

    /// <summary>
    /// Creates a session, has the portal select its sources, which shows the system's sharing
    /// dialog unless a restore token covers them, and starts it.
    /// </summary>
    /// <exception cref="OperationCanceledException">The user declined to share.</exception>
    /// <exception cref="PlatformNotSupportedException">The portal cannot share the requested kind of content.</exception>
    public static async Task<ScreenCastSession> StartAsync(DBusConnection connection, ScreenCastCapabilities capabilities, ScreenCastRequest request,
        CancellationToken cancellationToken)
    {
        if ((capabilities.Sources & request.Sources) == 0)
            throw new PlatformNotSupportedException($"The desktop cannot share {(request.Sources == ScreenCastSources.Window ? "single windows" : "screens")}.");

        string token = DesktopPortal.NewToken();
        string sessionToken = DesktopPortal.NewToken();
        (PortalResponse response, Dictionary<string, VariantValue> results) = await DesktopPortal.RequestAsync(connection, token, () =>
        {
            MessageWriter writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: DesktopPortal.Service, path: DesktopPortal.ObjectPath, @interface: Interface,
                signature: "a{sv}", member: "CreateSession");
            writer.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["handle_token"] = VariantValue.String(token),
                ["session_handle_token"] = VariantValue.String(sessionToken),
            });
            return writer.CreateMessage();
        }, cancellationToken).ConfigureAwait(false);
        ThrowIfUnsuccessful(response);
        VariantValue sessionHandle = DesktopPortal.Unwrap(results["session_handle"]);
        string session = sessionHandle.Type == VariantValueType.ObjectPath ? sessionHandle.GetObjectPathAsString() : sessionHandle.GetString();

        try
        {
            var selection = new Dictionary<string, VariantValue>
            {
                ["types"] = VariantValue.UInt32((uint)request.Sources),
                ["multiple"] = VariantValue.Bool(request.Multiple),
            };
            uint cursor = request.IncludeCursor ? CursorEmbedded : CursorHidden;
            if ((capabilities.CursorModes & cursor) != 0)
                selection["cursor_mode"] = VariantValue.UInt32(cursor);
            if (request.Persist && capabilities.Version >= 4)
            {
                selection["persist_mode"] = VariantValue.UInt32(PersistUntilRevoked);
                if (!string.IsNullOrEmpty(request.RestoreToken))
                    selection["restore_token"] = VariantValue.String(request.RestoreToken);
            }

            token = DesktopPortal.NewToken();
            selection["handle_token"] = VariantValue.String(token);
            (response, _) = await DesktopPortal.RequestAsync(connection, token, () =>
            {
                MessageWriter writer = connection.GetMessageWriter();
                writer.WriteMethodCallHeader(destination: DesktopPortal.Service, path: DesktopPortal.ObjectPath, @interface: Interface,
                    signature: "oa{sv}", member: "SelectSources");
                writer.WriteObjectPath(session);
                writer.WriteDictionary(selection);
                return writer.CreateMessage();
            }, cancellationToken).ConfigureAwait(false);
            ThrowIfUnsuccessful(response);

            token = DesktopPortal.NewToken();
            (response, results) = await DesktopPortal.RequestAsync(connection, token, () =>
            {
                MessageWriter writer = connection.GetMessageWriter();
                writer.WriteMethodCallHeader(destination: DesktopPortal.Service, path: DesktopPortal.ObjectPath, @interface: Interface,
                    signature: "osa{sv}", member: "Start");
                writer.WriteObjectPath(session);
                writer.WriteString(string.Empty);
                writer.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = VariantValue.String(token) });
                return writer.CreateMessage();
            }, cancellationToken).ConfigureAwait(false);
            ThrowIfUnsuccessful(response);

            IReadOnlyList<ScreenCastStream> streams = results.TryGetValue("streams", out VariantValue value)
                ? ReadStreams(DesktopPortal.Unwrap(value))
                : [];
            if (streams.Count == 0)
                throw new InvalidOperationException("The system shared nothing to capture.");
            string? restoreToken = results.TryGetValue("restore_token", out VariantValue restore) ? DesktopPortal.Unwrap(restore).GetString() : null;
            return new ScreenCastSession(connection, session, streams, restoreToken);
        }
        catch
        {
            await DesktopPortal.CloseAsync(connection, session, DesktopPortal.SessionInterface).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A connection to PipeWire that reaches only the streams of this session.</summary>
    public Task<SafeFileHandle> OpenPipeWireRemoteAsync()
    {
        MessageWriter writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: DesktopPortal.Service, path: DesktopPortal.ObjectPath, @interface: Interface,
            signature: "oa{sv}", member: "OpenPipeWireRemote");
        writer.WriteObjectPath(handle);
        ArrayStart options = writer.WriteDictionaryStart();
        writer.WriteDictionaryEnd(options);
        return connection.CallMethodAsync(writer.CreateMessage(), static (message, _) => message.GetBodyReader().ReadHandle<SafeFileHandle>(), null);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) == 0)
            await DesktopPortal.CloseAsync(connection, handle, DesktopPortal.SessionInterface).ConfigureAwait(false);
    }

    private static void ThrowIfUnsuccessful(PortalResponse response)
    {
        switch (response)
        {
            case PortalResponse.Success:
                return;
            case PortalResponse.Cancelled:
                throw new OperationCanceledException("Screen sharing was declined.");
            default:
                throw new InvalidOperationException(
                    "The system could not start screen sharing. It needs a Wayland session whose desktop shares screens through " +
                    "PipeWire, such as KDE Plasma or GNOME.");
        }
    }

    private static List<ScreenCastStream> ReadStreams(VariantValue streams)
    {
        var result = new List<ScreenCastStream>(streams.Count);
        for (int i = 0; i < streams.Count; i++)
        {
            VariantValue stream = streams.GetItem(i);
            uint node = stream.GetItem(0).GetUInt32();
            Dictionary<string, VariantValue> properties = stream.GetItem(1).GetDictionary<string, VariantValue>();
            result.Add(new ScreenCastStream(
                node,
                properties.TryGetValue("id", out VariantValue id) ? DesktopPortal.Unwrap(id).GetString() : null,
                properties.TryGetValue("source_type", out VariantValue type) ? (ScreenCastSources)DesktopPortal.Unwrap(type).GetUInt32() : ScreenCastSources.Monitor,
                properties.TryGetValue("position", out VariantValue position) ? ReadPair(DesktopPortal.Unwrap(position)) : null,
                properties.TryGetValue("size", out VariantValue size) ? ReadPair(DesktopPortal.Unwrap(size)) : null));
        }

        return result;
    }

    private static (int, int) ReadPair(VariantValue pair) => (pair.GetItem(0).GetInt32(), pair.GetItem(1).GetInt32());
}
