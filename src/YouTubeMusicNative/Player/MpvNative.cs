using System.Runtime.InteropServices;

namespace YouTubeMusicNative.Player;

internal enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25,
}

internal enum MpvEndFileReason
{
    Eof = 0,
    Stop = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public IntPtr Name;
    public MpvFormat Format;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public MpvEndFileReason Reason;
    public int Error;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertNumEntries;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventLogMessage
{
    public IntPtr Prefix;
    public IntPtr Level;
    public IntPtr Text;
    public int LogLevel;
}

internal static partial class MpvNative
{
    private const string Lib = "libmpv-2.dll";

    [LibraryImport(Lib, EntryPoint = "mpv_create")]
    public static partial IntPtr Create();

    [LibraryImport(Lib, EntryPoint = "mpv_initialize")]
    public static partial int Initialize(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_terminate_destroy")]
    public static partial void TerminateDestroy(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_set_option_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetOptionString(IntPtr ctx, string name, string data);

    [LibraryImport(Lib, EntryPoint = "mpv_set_property_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetPropertyString(IntPtr ctx, string name, string data);

    [LibraryImport(Lib, EntryPoint = "mpv_set_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetProperty(IntPtr ctx, string name, MpvFormat format, ref double data);

    [LibraryImport(Lib, EntryPoint = "mpv_set_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetProperty(IntPtr ctx, string name, MpvFormat format, ref int data);

    [LibraryImport(Lib, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GetProperty(IntPtr ctx, string name, MpvFormat format, out double data);

    [LibraryImport(Lib, EntryPoint = "mpv_command")]
    public static partial int Command(IntPtr ctx, IntPtr args);

    [LibraryImport(Lib, EntryPoint = "mpv_command_async")]
    public static partial int CommandAsync(IntPtr ctx, ulong replyUserdata, IntPtr args);

    [LibraryImport(Lib, EntryPoint = "mpv_observe_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ObserveProperty(IntPtr ctx, ulong replyUserdata, string name, MpvFormat format);

    [LibraryImport(Lib, EntryPoint = "mpv_request_log_messages", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int RequestLogMessages(IntPtr ctx, string minLevel);

    [LibraryImport(Lib, EntryPoint = "mpv_wait_event")]
    public static partial IntPtr WaitEvent(IntPtr ctx, double timeout);

    [LibraryImport(Lib, EntryPoint = "mpv_wakeup")]
    public static partial void Wakeup(IntPtr ctx);

    [LibraryImport(Lib, EntryPoint = "mpv_error_string")]
    public static partial IntPtr ErrorString(int error);

    public static string ErrorText(int error) => Marshal.PtrToStringUTF8(ErrorString(error)) ?? error.ToString();

    /// <summary>Marshals a string[] into a NULL-terminated char** for mpv_command; caller frees with <see cref="FreeArgv"/>.</summary>
    public static IntPtr AllocArgv(string[] args)
    {
        var argv = Marshal.AllocHGlobal(IntPtr.Size * (args.Length + 1));
        for (int i = 0; i < args.Length; i++)
            Marshal.WriteIntPtr(argv, i * IntPtr.Size, Marshal.StringToCoTaskMemUTF8(args[i]));
        Marshal.WriteIntPtr(argv, args.Length * IntPtr.Size, IntPtr.Zero);
        return argv;
    }

    public static void FreeArgv(IntPtr argv, int count)
    {
        for (int i = 0; i < count; i++)
            Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(argv, i * IntPtr.Size));
        Marshal.FreeHGlobal(argv);
    }
}
