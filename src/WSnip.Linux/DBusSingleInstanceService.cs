using LightStudio.Logging;
using Tmds.DBus.Protocol;
using WSnip.Core.Platform;

namespace WSnip.Linux;

/// <summary>
/// Keeps one instance per session on the session bus: the first process owns the app id as its
/// bus name and serves an object that later launches forward their arguments to.
/// </summary>
/// <remarks>
/// A Flatpak sandbox always lets an app own its own id, so no bus policy is needed. Without a
/// reachable session bus every launch runs on its own.
/// </remarks>
internal sealed class DBusSingleInstanceService : ISingleInstanceService, IPathMethodHandler
{
    private const string BusService = "org.freedesktop.DBus";
    private const string BusPath = "/org/freedesktop/DBus";
    private const uint DoNotQueue = 0x4;
    private const uint PrimaryOwner = 1, AlreadyOwner = 4;
    private const int MaxArguments = 64;

    private readonly SessionBus bus;
    private readonly string appId;
    private readonly string @interface;
    private readonly ReadOnlyMemory<byte> introspection;
    private readonly Lock gate = new();
    private readonly List<IReadOnlyList<string>> early = [];
    private DBusConnection? connection;
    private bool listening;

    public DBusSingleInstanceService(SessionBus bus, string appId)
    {
        this.bus = bus;
        this.appId = appId;
        @interface = appId + ".Instance";
        Path = "/" + appId.Replace('.', '/');
        introspection = System.Text.Encoding.UTF8.GetBytes(
            $"""
            <interface name="{@interface}">
              <method name="Forward">
                <arg direction="in" type="as" name="arguments"/>
              </method>
            </interface>

            """);
    }

    public event EventHandler<IReadOnlyList<string>>? ArgumentsReceived;

    public string Path { get; }

    public bool HandlesChildPaths => false;

    public bool TryClaim()
    {
        DBusConnection candidate;
        try
        {
            candidate = bus.ConnectAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is DBusExceptionBase or InvalidOperationException)
        {
            AppLog.Warning("Instance", "The session bus is not available, so this launch runs on its own.", exception);
            return true;
        }

        // The object is served before the name is owned, so no forwarded launch finds it missing.
        candidate.AddMethodHandler(this);
        try
        {
            uint reply = RequestNameAsync(candidate, appId).GetAwaiter().GetResult();
            if (reply is PrimaryOwner or AlreadyOwner)
            {
                connection = candidate;
                return true;
            }
        }
        catch (DBusExceptionBase exception)
        {
            AppLog.Warning("Instance", $"The bus name {appId} could not be requested, so this launch runs on its own.", exception);
            return true;
        }

        candidate.RemoveMethodHandler(Path);
        return false;
    }

    public void StartListening()
    {
        if (connection is null)
            throw new InvalidOperationException("Only the primary instance listens for activations.");
        List<IReadOnlyList<string>> received;
        lock (gate)
        {
            listening = true;
            received = [.. early];
            early.Clear();
        }

        foreach (IReadOnlyList<string> arguments in received)
            ArgumentsReceived?.Invoke(this, arguments);
    }

    public async Task<bool> ForwardAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            DBusConnection current = await bus.ConnectAsync().ConfigureAwait(false);
            MessageWriter writer = current.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: appId, path: Path, @interface: @interface, signature: "as", member: "Forward");
            writer.WriteArray(arguments.Take(MaxArguments).ToArray());
            await current.CallMethodAsync(writer.CreateMessage()).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is DBusExceptionBase or InvalidOperationException or TimeoutException)
        {
            AppLog.Warning("Instance", "Could not reach the running instance.", exception);
            return false;
        }
    }

    public void Dispose()
    {
        connection?.RemoveMethodHandler(Path);
        connection = null;
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([introspection]);
            return default;
        }

        Message request = context.Request;
        if (request.InterfaceAsString != @interface || request.MemberAsString != "Forward" || request.SignatureAsString != "as")
            return default;

        string[] arguments = request.GetBodyReader().ReadArrayOfString();
        context.Reply(context.CreateReplyWriter(null).CreateMessage());
        if (arguments.Length > MaxArguments)
            return default;

        lock (gate)
        {
            if (!listening)
            {
                early.Add(arguments);
                return default;
            }
        }

        ArgumentsReceived?.Invoke(this, arguments);
        return default;
    }

    private static Task<uint> RequestNameAsync(DBusConnection connection, string name)
    {
        MessageWriter writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: BusService, path: BusPath, @interface: BusService, signature: "su", member: "RequestName");
        writer.WriteString(name);
        writer.WriteUInt32(DoNotQueue);
        return connection.CallMethodAsync(writer.CreateMessage(), static (message, _) => message.GetBodyReader().ReadUInt32(), null);
    }
}
