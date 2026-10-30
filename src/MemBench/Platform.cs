using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using MemServe.Native;

namespace MemBench;

/// <summary>
/// OS differences in one place. Linux gets the zero-copy and /proc fast paths; Windows (and macOS) use portable .NET
/// APIs. MEMBENCH_PORTABLE=1 forces the portable paths on Linux, to test them without a Windows machine.
/// </summary>
internal static partial class Platform
{
    public static readonly bool Linux = OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("MEMBENCH_PORTABLE") != "1";

    public static string Name => Linux ? "linux" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux (portable mode)" : "portable";

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    /// <summary>OS thread id of the calling thread (what /proc or Process.Threads reports).</summary>
    public static int CurrentThreadId()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return (int)GetCurrentThreadId();
            if (OperatingSystem.IsLinux())
                return int.Parse(Path.GetFileName(new FileInfo("/proc/thread-self").LinkTarget ?? "0"), CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or FormatException or EntryPointNotFoundException or DllNotFoundException) { }
        return 0;
    }

    /// <summary>Bulk-data threads yield to the sampling thread when the client saturates the CPU.</summary>
    public static void LowerThreadPriority()
    {
        if (Linux) { Libc.setpriority(Libc.PRIO_PROCESS, 0, Libc.BulkNice); return; }
        try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch (Exception e) when (e is PlatformNotSupportedException or ThreadStateException) { }
    }
}

/// <summary>CPU seconds used so far by given OS threads of this process.</summary>
internal sealed class ThreadCpu
{
    private readonly Process _proc = Process.GetCurrentProcess();

    public double[] Read(IReadOnlyList<int> tids)
    {
        var result = new double[tids.Count];
        if (Platform.Linux)
        {
            for (int i = 0; i < tids.Count; i++) result[i] = ReadProc(tids[i]);
            return result;
        }
        // Windows / portable: one pass over the process's threads
        var byId = new Dictionary<int, double>();
        try
        {
            _proc.Refresh();
            foreach (ProcessThread t in _proc.Threads)
            {
                try { byId[t.Id] = t.TotalProcessorTime.TotalSeconds; }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { } // thread just exited
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
        for (int i = 0; i < tids.Count; i++) result[i] = byId.GetValueOrDefault(tids[i]);
        return result;
    }

    private static double ReadProc(int tid)
    {
        if (tid <= 0) return 0;
        try
        {
            var stat = File.ReadAllText($"/proc/self/task/{tid}/stat");
            var f = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return (long.Parse(f[11], CultureInfo.InvariantCulture) + long.Parse(f[12], CultureInfo.InvariantCulture)) / 100.0;
        }
        catch { return 0; }
    }
}
