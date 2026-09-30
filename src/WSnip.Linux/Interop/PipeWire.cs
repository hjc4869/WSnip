using System.Runtime.InteropServices;

namespace WSnip.Linux.Interop;

/// <summary>The parts of libpipewire-0.3 that read frames from a stream.</summary>
internal static unsafe partial class PipeWire
{
    public const string Library = "libpipewire-0.3.so.0";

    public const int StreamStateError = -1;
    public const int StreamStateUnconnected = 0;
    public const int DirectionInput = 0;
    public const int StreamFlagAutoconnect = 1 << 0;
    public const int StreamFlagMapBuffers = 1 << 2;
    public const uint StreamEventsVersion = 2;

    private static int initialized;

    /// <summary>Whether libpipewire can be loaded.</summary>
    public static bool IsAvailable { get; } = NativeLibrary.TryLoad(Library, typeof(PipeWire).Assembly, null, out _);

    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref initialized, 1) == 0)
            pw_init(null, null);
    }

    [LibraryImport(Library)]
    private static partial void pw_init(int* argc, byte*** argv);

    [LibraryImport(Library)]
    public static partial nint pw_thread_loop_new(byte* name, nint properties);

    [LibraryImport(Library)]
    public static partial nint pw_thread_loop_get_loop(nint loop);

    [LibraryImport(Library)]
    public static partial int pw_thread_loop_start(nint loop);

    [LibraryImport(Library)]
    public static partial void pw_thread_loop_stop(nint loop);

    [LibraryImport(Library)]
    public static partial void pw_thread_loop_lock(nint loop);

    [LibraryImport(Library)]
    public static partial void pw_thread_loop_unlock(nint loop);

    [LibraryImport(Library)]
    public static partial void pw_thread_loop_destroy(nint loop);

    [LibraryImport(Library)]
    public static partial nint pw_context_new(nint loop, nint properties, nuint userDataSize);

    [LibraryImport(Library)]
    public static partial void pw_context_destroy(nint context);

    /// <summary>Connects to the PipeWire daemon of the session.</summary>
    [LibraryImport(Library, SetLastError = true)]
    public static partial nint pw_context_connect(nint context, nint properties, nuint userDataSize);

    /// <summary>Connects over a socket, which PipeWire closes on disconnect or error.</summary>
    [LibraryImport(Library, SetLastError = true)]
    public static partial nint pw_context_connect_fd(nint context, int fd, nint properties, nuint userDataSize);

    [LibraryImport(Library)]
    public static partial int pw_core_disconnect(nint core);

    [LibraryImport(Library)]
    public static partial nint pw_properties_new_string(byte* text);

    [LibraryImport(Library)]
    public static partial nint pw_stream_new(nint core, byte* name, nint properties);

    [LibraryImport(Library)]
    public static partial void pw_stream_add_listener(nint stream, void* listener, StreamEvents* events, nint data);

    [LibraryImport(Library)]
    public static partial int pw_stream_connect(nint stream, int direction, uint targetId, int flags, byte** parameters, uint parameterCount);

    [LibraryImport(Library)]
    public static partial PwBuffer* pw_stream_dequeue_buffer(nint stream);

    [LibraryImport(Library)]
    public static partial int pw_stream_queue_buffer(nint stream, PwBuffer* buffer);

    [LibraryImport(Library)]
    public static partial int pw_stream_disconnect(nint stream);

    [LibraryImport(Library)]
    public static partial void pw_stream_destroy(nint stream);

    /// <summary>Callbacks of a stream, laid out as pw_stream_events version 2.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct StreamEvents
    {
        public uint Version;
        public nint Destroy;
        public delegate* unmanaged[Cdecl]<nint, int, int, byte*, void> StateChanged;
        public nint ControlInfo;
        public nint IoChanged;
        public delegate* unmanaged[Cdecl]<nint, uint, byte*, void> ParamChanged;
        public nint AddBuffer;
        public nint RemoveBuffer;
        public delegate* unmanaged[Cdecl]<nint, void> Process;
        public nint Drained;
        public nint Command;
        public nint TriggerDone;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PwBuffer
    {
        public SpaBuffer* Buffer;
        public nint UserData;
        public ulong Size;
        public ulong Requested;
        public ulong Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpaBuffer
    {
        public uint MetaCount;
        public uint DataCount;
        public SpaMeta* Metas;
        public SpaData* Datas;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpaMeta
    {
        public uint Type;
        public uint Size;
        public void* Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpaData
    {
        public uint Type;
        public uint Flags;
        public long Fd;
        public uint MapOffset;
        public uint MaxSize;
        public void* Data;
        public SpaChunk* Chunk;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpaChunk
    {
        public const int FlagCorrupted = 1 << 0;

        public uint Offset;
        public uint Size;
        public int Stride;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpaMetaHeader
    {
        public const uint Type = 1;
        public const uint FlagCorrupted = 1 << 1;

        public uint Flags;
        public uint Offset;
        public long Pts;
        public long DtsOffset;
        public ulong Sequence;
    }

    /// <summary>Size of struct spa_hook, which the stream links into its listener list.</summary>
    public const int HookSize = 48;
}
