using System.Runtime.InteropServices;
using MemServe.Native;
using MemServe.Util;

namespace MemServe.Core;

/// <summary>
/// The physical data: one block of pseudo-random bytes that lives only in RAM.
/// On Linux it is a memfd (anonymous tmpfs file) so the kernel can sendfile() it with zero copies.
/// Virtual files of any size are produced by cycling through this block.
/// </summary>
public sealed unsafe class Backing : IDisposable
{
    public long Size { get; }
    public byte* Ptr { get; }
    /// <summary>memfd descriptor on Linux, -1 elsewhere.</summary>
    public int Fd { get; } = -1;
    public bool ZeroCopy => Fd >= 0;

    private Backing(long size, byte* ptr, int fd) { Size = size; Ptr = ptr; Fd = fd; }

    public static Backing Create(long size)
    {
        if (size < 1 << 20) size = 1 << 20;
        size = (size + 4095) & ~4095L;
        byte* ptr = null;
        int fd = -1;
        if (OperatingSystem.IsLinux())
        {
            Libc.Touch();
            fd = Libc.memfd_create("memserve-pattern", Libc.MFD_CLOEXEC);
            if (fd >= 0)
            {
                if (Libc.ftruncate(fd, size) != 0)
                    throw new IOException($"ftruncate(memfd, {size}) failed, errno {Libc.Errno}. Not enough RAM? Lower --backing.");
                var p = Libc.mmap(IntPtr.Zero, (nuint)size, Libc.PROT_READ | Libc.PROT_WRITE, Libc.MAP_SHARED, fd, 0);
                if (p == (IntPtr)(-1) || p == IntPtr.Zero)
                    throw new IOException($"mmap(memfd) failed, errno {Libc.Errno}");
                Libc.madvise(p, (nuint)size, Libc.MADV_HUGEPAGE);
                ptr = (byte*)p;
            }
            else
            {
                Log.Warn($"memfd_create unavailable (errno {Libc.Errno}); falling back to copy mode");
            }
        }
        if (ptr == null)
            ptr = (byte*)NativeMemory.AlignedAlloc((nuint)size, 4096);

        Fill(ptr, size);
        return new Backing(size, ptr, fd);
    }

    private static void Fill(byte* ptr, long size)
    {
        const long block = 32L << 20;
        long blocks = (size + block - 1) / block;
        ulong seed = 0x9E3779B97F4A7C15UL ^ (ulong)DateTime.UtcNow.Ticks;
        Parallel.For(0, blocks, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, b =>
        {
            long start = b * block;
            long len = Math.Min(block, size - start);
            ulong s = seed + (ulong)b * 0xD1B54A32D192ED03UL;
            ulong* p = (ulong*)(ptr + start);
            long n = len / 8;
            for (long i = 0; i < n; i++)
            {
                // splitmix64
                s += 0x9E3779B97F4A7C15UL;
                ulong z = s;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                p[i] = z ^ (z >> 31);
            }
        });
    }

    /// <summary>Returns the contiguous piece of pattern starting at a virtual offset (never longer than one period).</summary>
    public byte* Slice(long virtualOffset, long maxCount, out long length)
    {
        long off = virtualOffset % Size;
        length = Math.Min(maxCount, Size - off);
        return Ptr + off;
    }

    public void Dispose()
    {
        if (Fd >= 0) Libc.close(Fd);
    }
}
