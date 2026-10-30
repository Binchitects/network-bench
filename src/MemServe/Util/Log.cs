namespace MemServe.Util;

public static class Log
{
    public static bool Verbose;
    public static bool Quiet;
    private static readonly object Gate = new();

    public static void Info(string msg) { if (!Quiet) Write(msg); }
    public static void Warn(string msg) => Write("WARN " + msg);
    public static void Error(string msg) => Write("ERROR " + msg);
    public static void Debug(string msg) { if (Verbose) Write("  " + msg); }

    private static void Write(string msg)
    {
        lock (Gate) Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
    }
}
