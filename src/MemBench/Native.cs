using System.Runtime.InteropServices;

namespace MemServe.Native;

/// <summary>Client-only libc bindings (the rest is shared with the server).</summary>
internal static unsafe partial class Libc
{
    public const int O_RDONLY = 0, O_CREAT = 0x40;
    public static int O_DIRECT => RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x10000 : 0x4000;

    public const int SOL_TLS = 282, TLS_GET_RECORD_TYPE = 2, EIO = 5;

    /// <summary>struct msghdr / iovec / cmsghdr for 64-bit Linux (musl's padded int fields are zero, so the layout matches).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IoVec { public byte* Base; public nuint Len; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MsgHdr
    {
        public IntPtr Name; public ulong NameLen; public IoVec* Iov; public nuint IovLen;
        public byte* Control; public nuint ControlLen; public long Flags;
    }

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint recvmsg(int fd, MsgHdr* msg, int flags);


    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint pread(int fd, byte* buf, nuint count, long offset);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int connect(int fd, byte* addr, int addrlen);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint pwrite(int fd, byte* buf, nuint count, long offset);

    [LibraryImport(Lib, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int OpenMode(string path, int flags, int mode);
}
