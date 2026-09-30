using LightStudio.Logging;
using Tmds.DBus.Protocol;

namespace WSnip.Linux.Portal;

/// <summary>How a portal request ended.</summary>
internal enum PortalResponse : uint
{
    Success = 0,
    Cancelled = 1,
    Failed = 2,
}

/// <summary>
/// Calls into org.freedesktop.portal.Desktop. Methods that involve the user answer through a
/// Request object, which emits one Response signal once the user or the system has decided.
/// </summary>
internal static class DesktopPortal
{
    public const string Service = "org.freedesktop.portal.Desktop";
    public const string ObjectPath = "/org/freedesktop/portal/desktop";
    public const string RequestInterface = "org.freedesktop.portal.Request";
    public const string SessionInterface = "org.freedesktop.portal.Session";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string RegistryInterface = "org.freedesktop.host.portal.Registry";
    private static int nextToken;

    /// <summary>A handle token, unique within this process.</summary>
    public static string NewToken() => $"wsnip{Environment.ProcessId}_{Interlocked.Increment(ref nextToken)}";

    /// <summary>
    /// Calls a method that takes a <c>handle_token</c> option and waits for its response. The
    /// response is watched before the call, as the portal may answer before the call returns.
    /// </summary>
    /// <param name="createCall">Writes the method call, passing <paramref name="token"/> as the handle_token option.</param>
    public static async Task<(PortalResponse Response, Dictionary<string, VariantValue> Results)> RequestAsync(
        DBusConnection connection, string token, Func<MessageBuffer> createCall, CancellationToken cancellationToken)
    {
        string sender = (connection.UniqueName ?? throw new InvalidOperationException("The session bus connection has no name."))
            .TrimStart(':').Replace('.', '_');
        string requestPath = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";
        var response = new TaskCompletionSource<(PortalResponse, Dictionary<string, VariantValue>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Path = requestPath,
            Interface = RequestInterface,
            Member = "Response",
        };
        using IDisposable watch = await connection.AddMatchAsync(rule,
            static (message, _) =>
            {
                Reader reader = message.GetBodyReader();
                var code = (PortalResponse)reader.ReadUInt32();
                return (code, reader.ReadDictionaryOfStringToVariantValue());
            },
            notification =>
            {
                if (notification.IsCompletion)
                    response.TrySetException(notification.Exception ?? new InvalidOperationException("The session bus connection closed."));
                else
                    response.TrySetResult(notification.Value);
            },
            emitOnCapturedContext: false, ObserverFlags.EmitOnConnectionClosed).ConfigureAwait(false);

        string handle = await connection.CallMethodAsync(createCall(),
            static (message, _) => message.GetBodyReader().ReadObjectPathAsString(), null).ConfigureAwait(false);
        if (handle != requestPath)
            throw new InvalidOperationException($"The desktop portal answers on {handle} rather than {requestPath}.");

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            if (response.TrySetCanceled(cancellationToken))
                _ = CloseAsync(connection, requestPath, RequestInterface);
        });
        return await response.Task.ConfigureAwait(false);
    }

    /// <summary>Reads a property of a portal interface.</summary>
    public static async Task<VariantValue> GetPropertyAsync(DBusConnection connection, string @interface, string property)
    {
        VariantValue value = await connection.CallMethodAsync(CreateMessage(),
            static (message, _) => message.GetBodyReader().ReadVariantValue(), null).ConfigureAwait(false);
        return Unwrap(value);

        MessageBuffer CreateMessage()
        {
            MessageWriter writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: Service, path: ObjectPath, @interface: PropertiesInterface, signature: "ss", member: "Get");
            writer.WriteString(@interface);
            writer.WriteString(property);
            return writer.CreateMessage();
        }
    }

    /// <summary>Closes a request or session object that the portal created.</summary>
    public static async Task CloseAsync(DBusConnection connection, string path, string @interface)
    {
        try
        {
            MessageWriter writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: Service, path: path, @interface: @interface, member: "Close");
            await connection.CallMethodAsync(writer.CreateMessage()).ConfigureAwait(false);
        }
        catch (DBusExceptionBase exception)
        {
            // The object is gone when the portal or the user ended it first.
            AppLog.Debug("Portal", $"Closing {path}: {exception.Message}");
        }
    }

    /// <summary>
    /// Tells the portal which app this unsandboxed process is, so its dialogs name the app and
    /// permissions such as restoring a screen selection persist. It must precede any other portal
    /// call on the connection; sandboxed apps are known to the portal already.
    /// </summary>
    public static async Task RegisterHostAppAsync(DBusConnection connection, string appId)
    {
        try
        {
            MessageWriter writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: Service, path: ObjectPath, @interface: RegistryInterface, signature: "sa{sv}", member: "Register");
            writer.WriteString(appId);
            ArrayStart options = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(options);
            await connection.CallMethodAsync(writer.CreateMessage()).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException exception)
        {
            // Portals before version 1.19 have no registry and identify host apps without it.
            AppLog.Information("Portal", $"The desktop portal did not register the app: {exception.ErrorName}: {exception.ErrorMessage}");
        }
    }

    /// <summary>A variant's value, looking through a variant that wraps another variant.</summary>
    public static VariantValue Unwrap(VariantValue value) =>
        value.Type == VariantValueType.Variant ? Unwrap(value.GetVariantValue()) : value;

    /// <summary>Whether a D-Bus error means the portal or one of its interfaces does not exist.</summary>
    public static bool IsMissing(DBusErrorReplyException exception) => exception.ErrorName is
        "org.freedesktop.DBus.Error.ServiceUnknown" or
        "org.freedesktop.DBus.Error.NameHasNoOwner" or
        "org.freedesktop.DBus.Error.UnknownObject" or
        "org.freedesktop.DBus.Error.UnknownInterface" or
        "org.freedesktop.DBus.Error.UnknownMethod" or
        "org.freedesktop.DBus.Error.UnknownProperty" or
        "org.freedesktop.DBus.Error.InvalidArgs";
}
