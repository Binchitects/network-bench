using System.Diagnostics;
using System.Text;
using MemServe.Util;

namespace MemServe.Core;

public sealed class ProtoStats(string name)
{
    public string Name = name;
    public long BytesOut, BytesIn, ConnectionsActive, ConnectionsTotal, Transfers;
    public double RateOut, RateIn; // bytes/s over the last interval
    internal long LastOut, LastIn;

    public void AddOut(long n) => Interlocked.Add(ref BytesOut, n);
    public void AddIn(long n) => Interlocked.Add(ref BytesIn, n);
    public void Connected() { Interlocked.Increment(ref ConnectionsActive); Interlocked.Increment(ref ConnectionsTotal); }
    public void Disconnected() => Interlocked.Decrement(ref ConnectionsActive);
    public void Transfer() => Interlocked.Increment(ref Transfers);
}

public sealed class Stats
{
    public readonly ProtoStats Http = new("http"), Https = new("https"), Ftp = new("ftp");
    public readonly Stopwatch Uptime = Stopwatch.StartNew();
    /// <summary>JSON fragment describing what this server offers (ports, TLS, SMB share), set at start-up.</summary>
    public string ServerInfo = "";
    public IEnumerable<ProtoStats> All => [Http, Https, Ftp];

    public void StartReporter(int intervalSeconds)
    {
        if (intervalSeconds <= 0) return;
        var t = new Thread(() =>
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                Thread.Sleep(intervalSeconds * 1000);
                double dt = sw.Elapsed.TotalSeconds; sw.Restart();
                bool any = false;
                var sb = new StringBuilder();
                foreach (var p in All)
                {
                    long o = Interlocked.Read(ref p.BytesOut), i = Interlocked.Read(ref p.BytesIn);
                    p.RateOut = (o - p.LastOut) / dt; p.RateIn = (i - p.LastIn) / dt;
                    p.LastOut = o; p.LastIn = i;
                    if (p.RateOut > 0 || p.RateIn > 0 || p.ConnectionsActive > 0) any = true;
                    sb.Append($"{p.Name.ToUpperInvariant(),-4} out {Sizes.Gbps(p.RateOut),12}  in {Sizes.Gbps(p.RateIn),12}  conns {p.ConnectionsActive,3} | ");
                }
                if (any && !Log.Quiet) Log.Info(sb.ToString().TrimEnd(' ', '|'));
            }
        }) { IsBackground = true, Name = "stats" };
        t.Start();
    }

    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append($"{{\"uptime_s\":{Uptime.Elapsed.TotalSeconds:0.0}");
        foreach (var p in All)
        {
            sb.Append($",\"{p.Name}\":{{\"bytes_out\":{Interlocked.Read(ref p.BytesOut)},\"bytes_in\":{Interlocked.Read(ref p.BytesIn)}," +
                      $"\"rate_out_gbps\":{p.RateOut * 8 / 1e9:0.000},\"rate_in_gbps\":{p.RateIn * 8 / 1e9:0.000}," +
                      $"\"connections_active\":{p.ConnectionsActive},\"connections_total\":{p.ConnectionsTotal},\"transfers\":{p.Transfers}}}");
        }
        if (ServerInfo.Length > 0) sb.Append(",\"server\":").Append(ServerInfo);
        AppendCpu(sb);
        sb.Append($",\"tls\":{{\"ktls_tx\":{Interlocked.Read(ref TlsCounters.KtlsTx)},\"ktls_rx\":{Interlocked.Read(ref TlsCounters.KtlsRx)},\"userspace\":{Interlocked.Read(ref TlsCounters.Userspace)}}}");
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// Process CPU and per-thread CPU (cumulative seconds), so a client can tell from two samples whether the
    /// server - or one of its connection threads - is saturated. Linux reads /proc/self/task; elsewhere only the total.
    /// </summary>
    private static void AppendCpu(StringBuilder sb)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        sb.Append(",\"cpus\":").Append(Environment.ProcessorCount);
        sb.Append(",\"cpu_seconds\":").Append(Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds.ToString("0.000", inv));
        if (!OperatingSystem.IsLinux()) return;
        sb.Append(",\"threads\":{");
        bool first = true;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc/self/task"))
            {
                string stat;
                try { stat = File.ReadAllText(Path.Combine(dir, "stat")); } catch (IOException) { continue; }
                // fields after the ")" of the comm: state is field 3, utime/stime are fields 14/15 (clock ticks)
                var f = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                if (f.Length < 13 || !long.TryParse(f[11], out var ut) || !long.TryParse(f[12], out var st)) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(Path.GetFileName(dir)).Append("\":").Append(((ut + st) / 100.0).ToString("0.00", inv));
            }
        }
        catch (DirectoryNotFoundException) { }
        sb.Append('}');
    }
}
