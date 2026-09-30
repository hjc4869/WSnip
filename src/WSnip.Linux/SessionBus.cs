using Tmds.DBus.Protocol;
using WSnip.Linux.Portal;

using WSnip.Core.Strings;

namespace WSnip.Linux;

/// <summary>The user's session bus: one connection, opened on first use and shared by the platform services.</summary>
/// <param name="hostAppId">The app id to introduce to the desktop portal, for a process outside a sandbox.</param>
internal sealed class SessionBus(string? hostAppId) : IDisposable
{
    private readonly Lock gate = new();
    private Task<DBusConnection>? connecting;
    private (DBusConnection Connection, Task Registration)? portal;

    public Task<DBusConnection> ConnectAsync()
    {
        lock (gate)
        {
            // A failed attempt is not kept, so the bus is tried again the next time it is needed.
            if (connecting is null || connecting.IsFaulted || connecting.IsCanceled)
                connecting = ConnectCoreAsync();
            return connecting;
        }
    }

    /// <summary>The connection, once the desktop portal knows which app it belongs to.</summary>
    public async Task<DBusConnection> ConnectToPortalAsync()
    {
        DBusConnection connection = await ConnectAsync().ConfigureAwait(false);
        if (hostAppId is null)
            return connection;

        Task registration;
        lock (gate)
        {
            if (portal is not { } known || known.Connection != connection)
                portal = known = (connection, DesktopPortal.RegisterHostAppAsync(connection, hostAppId));
            registration = known.Registration;
        }

        await registration.ConfigureAwait(false);
        return connection;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (connecting is { IsCompletedSuccessfully: true })
                connecting.Result.Dispose();
            connecting = null;
        }
    }

    private static async Task<DBusConnection> ConnectCoreAsync()
    {
        string address = DBusAddress.Session is { Length: > 0 } session
            ? session
            : throw new InvalidOperationException(AppStrings.NoSessionBus);
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
