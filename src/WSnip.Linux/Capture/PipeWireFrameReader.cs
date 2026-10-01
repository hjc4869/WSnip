using System.Diagnostics;
using System.Runtime.InteropServices;
using LightStudio.Logging;
using Microsoft.Win32.SafeHandles;
using WSnip.Core.Imaging;
using WSnip.Core.Strings;
using WSnip.Linux.Interop;
using static WSnip.Linux.Interop.PipeWire;

namespace WSnip.Linux.Capture;

/// <summary>One frame read from a stream: tightly packed rows of 8-bit pixels.</summary>
internal sealed record PipeWireFrame(uint NodeId, SpaVideoInfo Info, PixelBuffer<byte> Pixels) : IDisposable
{
    public void Dispose() => Pixels.Dispose();
}

/// <summary>
/// Reads the first complete frame of each of a set of PipeWire streams, over a connection the
/// ScreenCast portal opened or to the session's PipeWire daemon. PipeWire runs the streams on a
/// thread loop of its own, whose lock guards every call into them.
/// </summary>
internal static class PipeWireFrameReader
{
    private static readonly unsafe StreamEvents* Events = CreateEvents();

    /// <param name="remote">The portal's PipeWire connection, or null for the session's PipeWire daemon.</param>
    /// <param name="alpha">Prefers pixel formats with alpha, which keep a window's transparency.</param>
    /// <exception cref="TimeoutException">A stream delivered no frame in time.</exception>
    public static async Task<IReadOnlyList<PipeWireFrame>> ReadAsync(SafeFileHandle? remote, IReadOnlyList<uint> nodes, bool alpha, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        (nint loop, nint context) = StartLoop();
        var streams = new List<CaptureStream>(nodes.Count);
        nint core = 0;
        bool delivered = false;
        try
        {
            pw_thread_loop_lock(loop);
            try
            {
                if (remote is null)
                {
                    core = pw_context_connect(context, 0, 0);
                }
                else
                {
                    // PipeWire owns the socket from here on and closes it on disconnect or error.
                    int fd = (int)remote.DangerousGetHandle();
                    remote.SetHandleAsInvalid();
                    core = pw_context_connect_fd(context, fd, 0, 0);
                }

                if (core == 0)
                    throw new InvalidOperationException(string.Format(AppStrings.PipeWireConnectFailed, Marshal.GetLastPInvokeError()));

                byte[] formats = SpaPod.CaptureFormats(alpha);
                foreach (uint node in nodes)
                    streams.Add(CaptureStream.Connect(core, node, formats));
            }
            finally
            {
                pw_thread_loop_unlock(loop);
            }

            // A stream that fails ends the wait at once instead of when the others time out.
            Task<PipeWireFrame>[] frames = streams.Select(stream => stream.Frame.Task).ToArray();
            var pending = new List<Task<PipeWireFrame>>(frames);
            var elapsed = Stopwatch.StartNew();
            while (pending.Count > 0)
            {
                Task<PipeWireFrame> done;
                try
                {
                    TimeSpan remaining = timeout - elapsed.Elapsed;
                    done = await Task.WhenAny(pending).WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    uint[] missing = streams.Where(s => !s.Frame.Task.IsCompleted).Select(s => s.Node).ToArray();
                    throw new TimeoutException(string.Format(AppStrings.PipeWireNoFrame, string.Join(", ", missing)));
                }

                pending.Remove(done);
                await done.ConfigureAwait(false);
            }

            PipeWireFrame[] result = frames.Select(frame => frame.Result).ToArray();
            delivered = true;
            return result;
        }
        finally
        {
            pw_thread_loop_lock(loop);
            foreach (CaptureStream stream in streams)
            {
                stream.Destroy();
                if (!delivered && stream.Frame.Task.IsCompletedSuccessfully)
                    stream.Frame.Task.Result.Dispose();
            }
            if (core != 0)
                pw_core_disconnect(core);
            pw_thread_loop_unlock(loop);
            pw_thread_loop_stop(loop);
            pw_context_destroy(context);
            pw_thread_loop_destroy(loop);
        }
    }

    private static unsafe (nint Loop, nint Context) StartLoop()
    {
        nint loop;
        fixed (byte* name = "wsnip-capture"u8)
            loop = pw_thread_loop_new(name, 0);
        if (loop == 0)
            throw new InvalidOperationException(AppStrings.PipeWireLoopFailed);
        nint context = pw_context_new(pw_thread_loop_get_loop(loop), 0, 0);
        if (context == 0 || pw_thread_loop_start(loop) < 0)
        {
            if (context != 0)
                pw_context_destroy(context);
            pw_thread_loop_destroy(loop);
            throw new InvalidOperationException(AppStrings.PipeWireStartFailed);
        }

        return (loop, context);
    }

    private static unsafe StreamEvents* CreateEvents()
    {
        var events = (StreamEvents*)NativeMemory.AllocZeroed((nuint)sizeof(StreamEvents));
        events->Version = StreamEventsVersion;
        events->StateChanged = &OnStateChanged;
        events->ParamChanged = &OnParamChanged;
        events->Process = &OnProcess;
        return events;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnStateChanged(nint data, int old, int state, byte* error)
    {
        CaptureStream stream = CaptureStream.From(data);
        if (state == StreamStateError)
        {
            string message = Marshal.PtrToStringUTF8((nint)error) ?? "unknown error";
            stream.Frame.TrySetException(new InvalidOperationException(string.Format(AppStrings.PipeWireStreamFailed, stream.Node, message)));
        }
        else if (state == StreamStateUnconnected && old != StreamStateUnconnected)
        {
            stream.Frame.TrySetException(new InvalidOperationException(string.Format(AppStrings.PipeWireStreamEnded, stream.Node)));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnParamChanged(nint data, uint id, byte* param)
    {
        if (id != SpaPod.ParamFormat || param == null)
            return;
        CaptureStream stream = CaptureStream.From(data);
        var pod = new ReadOnlySpan<byte>(param, 8 + (int)*(uint*)param);
        stream.Info = SpaPod.ReadVideoFormat(pod);
        if (stream.Info is { } info)
            AppLog.Debug("Capture", $"PipeWire node {stream.Node} delivers {info.Format} {info.Width}x{info.Height}.");
        else
            stream.Frame.TrySetException(new NotSupportedException(string.Format(AppStrings.PipeWireFormatUnsupported, stream.Node)));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe void OnProcess(nint data)
    {
        CaptureStream stream = CaptureStream.From(data);
        PwBuffer* buffer = pw_stream_dequeue_buffer(stream.Handle);
        if (buffer == null)
            return;
        try
        {
            if (!stream.Frame.Task.IsCompleted && stream.Info is { } info && TryCopy(buffer->Buffer, info) is { } pixels)
            {
                if (!stream.Frame.TrySetResult(new PipeWireFrame(stream.Node, info, pixels)))
                    pixels.Dispose();
            }
        }
        catch (Exception exception)
        {
            // Nothing may unwind into PipeWire's thread.
            stream.Frame.TrySetException(exception);
        }
        finally
        {
            pw_stream_queue_buffer(stream.Handle, buffer);
        }
    }

    /// <summary>Copies a buffer that holds a whole frame; buffers that carry only a pointer update are skipped.</summary>
    private static unsafe PixelBuffer<byte>? TryCopy(SpaBuffer* buffer, SpaVideoInfo info)
    {
        if (buffer == null || buffer->DataCount < 1)
            return null;
        for (uint i = 0; i < buffer->MetaCount; i++)
        {
            SpaMeta* meta = &buffer->Metas[i];
            if (meta->Type == SpaMetaHeader.Type && meta->Size >= sizeof(SpaMetaHeader) &&
                (((SpaMetaHeader*)meta->Data)->Flags & SpaMetaHeader.FlagCorrupted) != 0)
                return null;
        }

        SpaData* plane = &buffer->Datas[0];
        SpaChunk* chunk = plane->Chunk;
        if (plane->Data == null || chunk == null || chunk->Size == 0 || (chunk->Flags & SpaChunk.FlagCorrupted) != 0 || plane->MaxSize == 0)
            return null;

        int row = info.Width * 4;
        long stride = chunk->Stride > 0 ? chunk->Stride : row;
        long offset = chunk->Offset % plane->MaxSize;
        if (stride < row || offset + stride * (info.Height - 1) + row > plane->MaxSize)
            return null;

        using var pixels = new PixelBuffer<byte>(checked(row * info.Height));
        byte* source = (byte*)plane->Data + offset;
        fixed (byte* target = pixels)
        {
            for (int y = 0; y < info.Height; y++)
                Buffer.MemoryCopy(source + y * stride, target + (long)y * row, row, row);
        }

        return pixels.Share();
    }

    /// <summary>A stream and the native memory PipeWire keeps a reference to while it exists.</summary>
    private sealed unsafe class CaptureStream
    {
        private GCHandle self;
        private void* hook;

        private CaptureStream(uint node) => Node = node;

        public uint Node { get; }

        public nint Handle { get; private set; }

        /// <summary>Set on PipeWire's thread once the format is negotiated.</summary>
        public SpaVideoInfo? Info { get; set; }

        public TaskCompletionSource<PipeWireFrame> Frame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static CaptureStream From(nint data) => (CaptureStream)GCHandle.FromIntPtr(data).Target!;

        /// <summary>Creates and connects a capture stream; the caller holds the thread loop lock.</summary>
        public static CaptureStream Connect(nint core, uint node, byte[] formats)
        {
            var stream = new CaptureStream(node);
            nint properties;

            // Only the requested node will do: another source would give a display the wrong picture.
            fixed (byte* text = "media.type=Video media.category=Capture media.role=Screen node.dont-fallback=true node.dont-reconnect=true"u8)
                properties = pw_properties_new_string(text);
            fixed (byte* name = "wsnip-capture"u8)
                stream.Handle = pw_stream_new(core, name, properties);
            if (stream.Handle == 0)
                throw new InvalidOperationException(string.Format(AppStrings.PipeWireCreateStreamFailed, node));

            stream.self = GCHandle.Alloc(stream);
            stream.hook = NativeMemory.AllocZeroed(HookSize);
            pw_stream_add_listener(stream.Handle, stream.hook, Events, GCHandle.ToIntPtr(stream.self));
            fixed (byte* pod = formats)
            {
                byte* parameters = pod;
                int result = pw_stream_connect(stream.Handle, DirectionInput, node, StreamFlagAutoconnect | StreamFlagMapBuffers, &parameters, 1);
                if (result < 0)
                {
                    stream.Destroy();
                    throw new InvalidOperationException(string.Format(AppStrings.PipeWireConnectNodeFailed, node, -result));
                }
            }

            return stream;
        }

        /// <summary>Disconnects and frees the stream; the caller holds the thread loop lock.</summary>
        public void Destroy()
        {
            if (Handle != 0)
            {
                pw_stream_disconnect(Handle);
                pw_stream_destroy(Handle);
                Handle = 0;
            }

            if (hook != null)
            {
                NativeMemory.Free(hook);
                hook = null;
            }

            if (self.IsAllocated)
                self.Free();
            Frame.TrySetCanceled();
        }
    }
}
