using System.Runtime.InteropServices;

namespace Wisp.Core;

internal static class NativeMethods
{
    internal const string Library = "wisp_core";

    [StructLayout(LayoutKind.Sequential)]
    internal struct EngineConfigNative
    {
        public IntPtr Name;
        public IntPtr SaveDirectory;
        public IntPtr IdentityDirectory;
        public int ListenPort;
        public int DiscoveryPort;
        public int AutoAccept;
        public int DataChannels;
        public int ConflictPolicy;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void EventCallback(IntPtr user, int type, IntPtr json);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void LogCallback(IntPtr user, int level, IntPtr message);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wisp_engine_create(
        ref EngineConfigNative config,
        EventCallback eventCallback,
        LogCallback logCallback,
        IntPtr user);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int wisp_engine_start(IntPtr engine);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wisp_engine_stop(IntPtr engine);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wisp_engine_destroy(IntPtr engine);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int wisp_engine_send_paths(
        IntPtr engine,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string peerId,
        IntPtr[] paths,
        UIntPtr pathCount,
        IntPtr transferIdOut,
        UIntPtr transferIdOutSize);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int wisp_engine_respond(
        IntPtr engine,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string offerId,
        int accept,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? saveDirectory);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int wisp_engine_cancel(
        IntPtr engine,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string transferId);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int wisp_engine_add_peer(
        IntPtr engine,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string host,
        int port,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? fingerprint);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int wisp_engine_set_link_token(
        IntPtr engine,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? linkToken);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int wisp_engine_set_expected_link_token(
        IntPtr engine,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? expectedToken);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int wisp_engine_set_bind_address(
        IntPtr engine,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? address);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int wisp_engine_get_peers(IntPtr engine, IntPtr jsonOut, UIntPtr jsonOutSize);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int wisp_engine_get_self(IntPtr engine, IntPtr jsonOut, UIntPtr jsonOutSize);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr wisp_version();
}
