using System.Reflection;
using System.Runtime.InteropServices;

namespace MemServe.Native;

/// <summary>Thin libc bindings used for the Linux zero-copy data path.</summary>
internal static unsafe partial class Libc
{
    private const string Lib = "libc";

    static Libc()
    {
        NativeLibrary.SetDllImportResolver(typeof(Libc).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        string[] candidates = name switch
        {
            Lib => ["libc.so.6", "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1", "libc.so.7", "libc"],
            "libssl" => ["libssl.so.3", "libssl.3.dylib", "libssl"],          // OpenSSL 3, same library .NET's SslStream uses
            "libcrypto" => ["libcrypto.so.3", "libcrypto.3.dylib", "libcrypto"],
            _ => [],
        };
        foreach (var cand in candidates)
            if (NativeLibrary.TryLoad(cand, out var h)) return h;
        return IntPtr.Zero;
    }

    public static void Touch() { } // forces the static ctor (resolver registration)

    public const uint MFD_CLOEXEC = 0x0001;
    public const int PROT_READ = 1, PROT_WRITE = 2;
    public const int MAP_SHARED = 0x01, MAP_PRIVATE = 0x02, MAP_ANONYMOUS = 0x20, MAP_POPULATE = 0x8000;
    public const int MADV_HUGEPAGE = 14;
    public const int O_WRONLY = 1, O_CLOEXEC = 0x80000, O_NONBLOCK = 0x800;
    public const int F_SETPIPE_SZ = 1031, F_GETFL = 3, F_SETFL = 4;
    public const uint SPLICE_F_MOVE = 1, SPLICE_F_MORE = 4;
    public const int MSG_NOSIGNAL = 0x4000, MSG_MORE = 0x8000, MSG_TRUNC = 0x20;
    public const int EAGAIN = 11, EINTR = 4, EINVAL = 22, ENOSYS = 38, EPIPE = 32, ECONNRESET = 104, EOPNOTSUPP = 95;

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int memfd_create(string name, uint flags);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int ftruncate(int fd, long length);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial IntPtr mmap(IntPtr addr, nuint length, int prot, int flags, int fd, long offset);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int madvise(IntPtr addr, nuint length, int advice);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint sendfile(int out_fd, int in_fd, ref long offset, nuint count);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint splice(int fd_in, IntPtr off_in, int fd_out, IntPtr off_out, nuint len, uint flags);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int pipe2(int* fds, int flags);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int fcntl(int fd, int cmd, int arg);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint send(int fd, byte* buf, nuint len, int flags);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint recv(int fd, byte* buf, nuint len, int flags);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint recvmsg(int fd, void* msg, int flags);

    public const int IPPROTO_TCP = 6, TCP_INFO = 11;
    public const byte TCP_ESTABLISHED = 1;

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int getsockopt(int fd, int level, int optname, byte* optval, int* optlen);

    /// <summary>tcpi_state from TCP_INFO (first byte), or 0 if unavailable.</summary>
    public static byte TcpState(int fd)
    {
        byte* info = stackalloc byte[8];
        int len = 8;
        return getsockopt(fd, IPPROTO_TCP, TCP_INFO, info, &len) == 0 && len >= 1 ? info[0] : (byte)0;
    }

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int open(string path, int flags);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int close(int fd);

    /// <summary>On Linux the nice value is per thread: setpriority(PRIO_PROCESS, 0, n) changes only the calling thread.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int setpriority(int which, int who, int prio);

    public const int PRIO_PROCESS = 0;
    /// <summary>Nice value for threads that pump bulk data, so accept/control/stats threads stay responsive when CPU-bound.</summary>
    public const int BulkNice = 10;

    public static int Errno => Marshal.GetLastPInvokeError();
}
