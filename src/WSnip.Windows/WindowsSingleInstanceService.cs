using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using LightStudio.Logging;
using WSnip.Core.Platform;

namespace WSnip.Windows;

/// <summary>
/// Keeps one instance per session: the first process owns a named mutex and listens on a
/// current-user pipe, and later launches forward their arguments to it and exit.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSingleInstanceService : ISingleInstanceService
{
    private const int MaxArguments = 64;
    private const int MaxArgumentBytes = 32 * 1024;

    private readonly string mutexName;
    private readonly string pipeName;
    private Mutex? mutex;
    private CancellationTokenSource? listening;

    public WindowsSingleInstanceService(string appId)
    {
        int session = Process.GetCurrentProcess().SessionId;
        mutexName = $"Local\\{appId}.SingleInstance";
        pipeName = $"{appId}.{session}.Activation";
    }

    public event EventHandler<IReadOnlyList<string>>? ArgumentsReceived;

    public bool TryClaim()
    {
        var candidate = new Mutex(initiallyOwned: false, mutexName, out bool createdNew);
        if (!createdNew)
        {
            candidate.Dispose();
            return false;
        }

        mutex = candidate;
        return true;
    }

    public void StartListening()
    {
        if (mutex is null)
            throw new InvalidOperationException("Only the primary instance listens for activations.");
        listening = new CancellationTokenSource();
        _ = Task.Run(() => ListenAsync(listening.Token));
    }

    public async Task<bool> ForwardAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            await client.ConnectAsync(3000, cancellationToken).ConfigureAwait(false);
            byte[] payload = Encode(arguments);
            await client.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await client.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            AppLog.Warning("Instance", "Could not reach the running instance.", exception);
            return false;
        }
    }

    public void Dispose()
    {
        listening?.Cancel();
        listening?.Dispose();
        listening = null;
        mutex?.Dispose();
        mutex = null;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                IReadOnlyList<string>? arguments = await DecodeAsync(server, cancellationToken).ConfigureAwait(false);
                if (arguments is not null)
                    ArgumentsReceived?.Invoke(this, arguments);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                AppLog.Warning("Instance", "Ignored a malformed activation request.", exception);
            }
        }
    }

    private static byte[] Encode(IReadOnlyList<string> arguments)
    {
        using var stream = new MemoryStream();
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(number, Math.Min(arguments.Count, MaxArguments));
        stream.Write(number);
        foreach (string argument in arguments.Take(MaxArguments))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(argument);
            BinaryPrimitives.WriteInt32LittleEndian(number, bytes.Length);
            stream.Write(number);
            stream.Write(bytes);
        }

        return stream.ToArray();
    }

    private static async Task<IReadOnlyList<string>?> DecodeAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] number = new byte[4];
        await stream.ReadExactlyAsync(number, cancellationToken).ConfigureAwait(false);
        int count = BinaryPrimitives.ReadInt32LittleEndian(number);
        if (count is < 0 or > MaxArguments)
            throw new InvalidDataException("Too many arguments.");
        var arguments = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            await stream.ReadExactlyAsync(number, cancellationToken).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32LittleEndian(number);
            if (length is < 0 or > MaxArgumentBytes)
                throw new InvalidDataException("Argument too long.");
            byte[] bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            arguments.Add(Encoding.UTF8.GetString(bytes));
        }

        return arguments;
    }
}
