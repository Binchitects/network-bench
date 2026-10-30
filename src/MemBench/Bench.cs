using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MemServe.Core;

namespace MemBench;

/// <summary>Runs the workers, samples throughput and CPU on both ends, and explains where the bottleneck is.</summary>
internal sealed class Bench(BenchOptions o)
{
    private const double Saturated = 0.85;  // a thread above 85% of a core is treated as the limit
    private readonly Process _proc = Process.GetCurrentProcess();
    private readonly Stopwatch _sw = new();
    private readonly CancellationTokenSource _stop = new();
    private WorkerState[] _st = [];
    private readonly List<Snapshot> _snaps = [];
    private readonly List<string> _notes = [];
    private readonly ThreadCpu _threadCpu = new();
    private ServerStats? _server;
    private ServerStats.Sample? _before;   // server counters before the workers started

    private sealed record Snapshot(double T, long[] Bytes, double[] Cpu, double ProcCpu, ServerStats.Sample? Server);

    public int Run()
    {
        Backing? backing = o.Upload ? Backing.Create(o.BackingSize) : null; // uploads and file writes send the pattern
        _st = Enumerable.Range(0, o.Connections).Select(i => new WorkerState(i)).ToArray();
        string dir = o.Proto == Proto.File ? (o.Upload ? "write" : "read") : (o.Upload ? "upload" : "download");
        string path = o.Proto switch
        {
            Proto.Http => $"{(o.Tls ? "https" : "http")}://{o.Host}:{o.Port}{o.Path}",
            Proto.Ftp => $"{(o.Tls ? "ftpes" : "ftp")}://{o.Host}:{o.Port}/{o.Path}",
            _ => o.Path,
        };
        string direct = Platform.Linux ? "O_DIRECT" : OperatingSystem.IsWindows() ? "unbuffered" : "buffered (no direct I/O here)";
        string dataPath = o.Proto == Proto.File ? (o.Direct ? $"{direct}, block {Fmt.Size(o.Block)}" : $"buffered, block {Fmt.Size(o.Block)}")
                        : o.Tls ? (o.Upload ? "TLS, client encrypts" : o.TlsRxDiscard ? "TLS, ciphertext discarded unread (measures the server)" : "TLS, client decrypts")
                        : o.Upload ? $"tx={(o.TxSendfile && Platform.Linux ? "sendfile" : "copy")}" : $"rx={(o.RxTrunc && Platform.Linux ? "trunc" : "copy")}";
        if (!Platform.Linux && o.Proto != Proto.File) dataPath += $" ({Platform.Name}: no MSG_TRUNC/sendfile, data is copied)";
        Say($"membench -> {path}  {dir}, {o.Connections} {(o.Proto == Proto.File ? "thread" : "connection")}{(o.Connections > 1 ? "s" : "")}, " +
            $"{o.Seconds:0.#}s + {o.Warmup:0.#}s warmup, {dataPath}");

        string? statsUrl = null;
        if (!o.NoStats)
        {
            statsUrl = o.StatsUrl ?? (o.Proto switch
            {
                Proto.Http => $"{(o.Tls ? "https" : "http")}://{Fmt.HostPart(o.Host)}:{o.Port}/stats",
                Proto.Ftp => $"http://{Fmt.HostPart(o.Host)}/stats",
                _ => null,
            });
            if (statsUrl != null) _server = ServerStats.Probe(statsUrl, o.Insecure, out _before);
            // a previous run's connections can still be draining on the server (kernel TLS keeps sending for up to
            // ~1 s after a client resets); measuring on top of them would be wrong, so wait for them first
            var drain = Stopwatch.StartNew();
            long leftover = (_before?.Active ?? 1) - 1; // minus our own /stats connection
            while (_server != null && leftover > 0 && drain.Elapsed.TotalSeconds < 5)
            {
                Thread.Sleep(100);
                _before = _server.Take() ?? _before;
                leftover = (_before?.Active ?? 1) - 1;
            }
            if (drain.Elapsed.TotalSeconds >= 0.3)
                _notes.Add(leftover > 0 ? $"{leftover} other connections were still active on the server; the server is shared or busy, results include their load"
                                        : $"waited {drain.Elapsed.TotalSeconds:0.0}s for the server to finish connections left from an earlier run");
        }

        var threads = _st.Select(s =>
        {
            Worker w = o.Proto switch
            {
                Proto.Http => new HttpWorker(o, s, backing, _stop.Token),
                Proto.Ftp => new FtpWorker(o, s, backing, _stop.Token),
                _ => new FileWorker(o, s, backing, _stop.Token),
            };
            var t = new Thread(w.ThreadMain, 256 * 1024) { IsBackground = true, Name = $"worker-{s.Index}" };
            t.Start();
            return t;
        }).ToArray();

        // measure only once every connection is actually moving data (connecting alone proves nothing)
        var connectBy = Stopwatch.StartNew();
        while (!_st.All(s => s.Ready || s.Done) && connectBy.Elapsed.TotalSeconds < 15) Thread.Sleep(5);
        foreach (var s in _st.Where(s => s.Error != null).GroupBy(s => s.Error)) Say($"  error ({s.Count()}x): {s.Key}");
        if (!_st.Any(s => s.Ready && !s.Done)) { Say("no worker could start; nothing measured"); return 1; }
        int idle = _st.Count(s => !s.Ready && !s.Done);
        if (idle > 0) _notes.Add($"{idle} of {o.Connections} connections had moved no data after 15s; the server is not serving them (overloaded or limited)");
        else Say($"  all {o.Connections} {(o.Proto == Proto.File ? "threads" : "connections")} moving data after {connectBy.Elapsed.TotalSeconds:0.00}s");

        var link = Net.LinkOf(_st.FirstOrDefault(s => s.Sock != null)?.Sock);
        if (_st.FirstOrDefault(s => s.TlsInfo != null)?.TlsInfo is { } ti) Say($"  tls: {ti}");
        if (o.Proto != Proto.File)
            Say("  link: " + (link switch
            {
                null => "unknown",
                { Loopback: true } => "same host (kernel loopback, no NIC involved)",
                { Bps: > 0 } l => $"{l.Name} {Fmt.Bits(l.Bps)}",
                var l => $"{l.Value.Name}, speed unknown",
            }));
        if (!o.NoStats)
        {
            var url = statsUrl;
            Say(_server != null ? $"  server: memserve stats at {url} ({_server.Cpus} cpus)"
                                : url != null ? $"  server: no memserve /stats at {url}; server CPU unknown (use --stats <url>)" : "  server: CPU unknown (pass --stats <url> for a memserve server)");
        }

        // ---- sampling
        _sw.Start();
        double total = o.Warmup + o.Seconds;
        var prev = Take();
        _snaps.Add(prev);
        Snapshot? start = o.Warmup <= 0 ? prev : null;
        for (int k = 1; ; k++)
        {
            double next = Math.Min(k * o.Interval, total);
            var wait = next - _sw.Elapsed.TotalSeconds;
            if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
            var cur = Take();
            _snaps.Add(cur);
            if (!o.Quiet && !o.Json) PrintInterval(prev, cur, cur.T <= o.Warmup + 1e-3);
            if (start == null && cur.T >= o.Warmup - 1e-3) start = cur;
            prev = cur;
            if (cur.T >= total - 1e-3 || _st.All(s => s.Done)) break;
        }
        var end = prev;
        start ??= end;

        // ---- stop: read TCP state first, then close the sockets to unblock the workers
        var tcp = _st.Select(s => Net.TcpInfo(s.Sock)).Where(t => t != null).Select(t => t!.Value).ToArray();
        // abort, don't close gracefully: an RST stops the server at once. (A graceful close can leave the server
        // streaming into the void for seconds, which slows down whatever test runs next.)
        _stop.Cancel();
        foreach (var s in _st) Net.Abort(s.Sock, s.KernelTls);
        var exitBy = Stopwatch.StartNew(); // one shared deadline, not one per thread
        foreach (var t in threads) t.Join(TimeSpan.FromMilliseconds(Math.Max(0, 2000 - exitBy.ElapsedMilliseconds)));
        foreach (var s in _st) { try { s.Sock?.Close(); } catch { } }
        if (o.Proto == Proto.File && o.Upload && !o.Keep) { try { File.Delete(o.Path); } catch (IOException) { } }

        return Summarize(start, end, link, tcp);
    }

    private Snapshot Take()
    {
        var bytes = _st.Select(s => Volatile.Read(ref s.Bytes)).ToArray();
        var cpu = _threadCpu.Read(_st.Select(s => s.Tid).ToArray());
        _proc.Refresh();
        double proc = _proc.TotalProcessorTime.TotalSeconds;
        double t = _sw.Elapsed.TotalSeconds;
        return new Snapshot(t, bytes, cpu, proc, _server?.Take());
    }


    private readonly record struct Window(double Seconds, long Bytes, long[] PerWorker, double ClientCores, double ClientTop,
                                          double? ServerBps, double? ServerCores, double? ServerTop);

    private Window Measure(Snapshot a, Snapshot b)
    {
        double dt = Math.Max(b.T - a.T, 1e-9);
        var per = b.Bytes.Zip(a.Bytes, (x, y) => x - y).ToArray();
        // one thread cannot exceed one core; /proc tick granularity can make a short window read slightly above 100%
        double top = Math.Min(1, b.Cpu.Zip(a.Cpu, (x, y) => x - y).DefaultIfEmpty(0).Max() / dt);
        double? sBps = null, sCores = null, sTop = null;
        if (a.Server != null && b.Server != null)
        {
            sBps = (b.Server.Bytes - a.Server.Bytes) * 8 / dt;
            sCores = (b.Server.Cpu - a.Server.Cpu) / dt;
            sTop = Math.Min(1, b.Server.Threads.Select(kv => kv.Value - a.Server.Threads.GetValueOrDefault(kv.Key)).DefaultIfEmpty(0).Max() / dt);
        }
        return new Window(dt, per.Sum(), per, (b.ProcCpu - a.ProcCpu) / dt, top, sBps, sCores, sTop);
    }

    private void PrintInterval(Snapshot a, Snapshot b, bool warmup)
    {
        var w = Measure(a, b);
        var line = $"  [{b.T,6:0.0}s] {Fmt.Bits(w.Bytes * 8 / w.Seconds),12}   client {w.ClientCores,5:0.00} cores, top thread {Fmt.Pct(w.ClientTop),4}";
        if (w.ServerBps is { } sb) line += $"   server {Fmt.Bits(sb),12}, {w.ServerCores,5:0.00} cores, top thread {Fmt.Pct(w.ServerTop),4}";
        if (warmup) line += "   (warmup)";
        Console.WriteLine(line);
    }

    private int Summarize(Snapshot a, Snapshot b, (string Name, long Bps, bool Loopback)? link, Net.TcpStats[] tcp)
    {
        var w = Measure(a, b);
        // a /stats poll can time out when the server is swamped: use the outermost samples that did arrive
        var sa = _snaps.FirstOrDefault(x => x.T >= a.T && x.Server != null);
        var sb = _snaps.LastOrDefault(x => x.T <= b.T && x.Server != null);
        if (w.ServerBps == null && sa != null && sb != null && sb.T > sa.T)
        {
            var sw = Measure(sa, sb);
            w = w with { ServerBps = sw.ServerBps, ServerCores = sw.ServerCores, ServerTop = sw.ServerTop };
        }
        double bps = w.Bytes * 8 / w.Seconds;
        double rttMs = tcp.Length > 0 ? tcp.Average(t => Math.Max(t.RttMs, t.RcvRttMs)) : 0;
        long retrans = tcp.Sum(t => t.Retrans);
        var errors = _st.Where(s => s.Error != null).Select(s => s.Error!).Distinct().ToArray();
        var notes = _st.Where(s => s.Note != null).Select(s => s.Note!).Distinct().Concat(_notes).ToList();
        if (MemServe.Util.CpuInfo.HybridNote() is { } cpuNote) notes.Add("client " + cpuNote);
        if (o.Connections > 1 && w.Bytes > 0)
        {
            double avg = (double)w.Bytes / o.Connections;
            int starved = w.PerWorker.Count(x => x < 0.1 * avg);
            if (starved > 0)
                notes.Add($"{starved} of {o.Connections} {(o.Proto == Proto.File ? "threads" : "connections")} got under 10% of the average rate (slowest {Fmt.Bits(w.PerWorker.Min() * 8 / w.Seconds)}): bandwidth is not shared fairly");
        }
        string? serverTls = null;
        if (o.Tls && _before != null && _snaps.LastOrDefault(x => x.Server != null)?.Server is { } after)
        {
            long k = after.KtlsTx - _before.KtlsTx, u = after.TlsUserspace - _before.TlsUserspace;
            if (k + u > 0) serverTls = u == 0 ? "kernel (kTLS)" : k == 0 ? "user space (no kTLS)" : $"mixed: {k} kTLS, {u} user space";
        }
        var (verdict, why, hint) = Diagnose(w, bps, link, rttMs);
        if (serverTls?.StartsWith("user space") == true && verdict.StartsWith("SERVER"))
            hint += "; memserve encrypts in user space: `sudo modprobe tls` on the server enables kernel TLS (about +50%)";

        if (o.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                target = o.Target, mode = o.Upload ? "upload" : "download", connections = o.Connections, seconds = Math.Round(w.Seconds, 3),
                bytes = w.Bytes, gbps = Math.Round(bps / 1e9, 3), per_connection_gbps = w.PerWorker.Select(x => Math.Round(x * 8 / w.Seconds / 1e9, 3)),
                client_cores = Math.Round(w.ClientCores, 3), client_top_thread = Math.Round(w.ClientTop, 3),
                server_gbps = w.ServerBps is { } s ? Math.Round(s / 1e9, 3) : (double?)null,
                server_cores = w.ServerCores is { } c ? Math.Round(c, 3) : (double?)null,
                server_top_thread = w.ServerTop is { } t ? Math.Round(t, 3) : (double?)null,
                link = link?.Name, link_gbps = link is { Bps: > 0 } l ? l.Bps / 1e9 : (double?)null,
                rtt_ms = Math.Round(rttMs, 3), retransmits = retrans, server_tls = serverTls, verdict, reason = why, hint, errors, notes,
            }));
            return w.Bytes > 0 ? 0 : 1;
        }

        Console.WriteLine();
        Console.WriteLine($"Result: {Fmt.Bits(bps)} ({Fmt.Size(w.Bytes / w.Seconds)}/s) over {w.Seconds:0.0}s, {Fmt.Size(w.Bytes)} moved");
        if (o.Connections > 1)
        {
            var per = w.PerWorker.Select(x => x * 8 / w.Seconds).ToArray();
            Console.WriteLine($"  per {(o.Proto == Proto.File ? "thread" : "connection")}: min {Fmt.Bits(per.Min())}  avg {Fmt.Bits(per.Average())}  max {Fmt.Bits(per.Max())}");
        }
        Console.WriteLine($"  client: {w.ClientCores:0.00} cores, busiest thread {Fmt.Pct(w.ClientTop)} of a core");
        if (w.ServerTop is { } st) Console.WriteLine($"  server: {w.ServerCores:0.00} cores, busiest thread {Fmt.Pct(st)} of a core, server counted {Fmt.Bits(w.ServerBps ?? 0)}");
        if (tcp.Length > 0) Console.WriteLine($"  tcp: rtt {rttMs:0.###} ms" + (o.Upload ? $", retransmits {retrans}" : ""));
        if (serverTls != null) Console.WriteLine($"  server tls: {serverTls}");
        foreach (var e in errors) Console.WriteLine($"  error: {e}");
        foreach (var n in notes) Console.WriteLine($"  note: {n}");
        Console.WriteLine();
        Console.WriteLine($"Verdict: {verdict} - {why}");
        if (hint.Length > 0) Console.WriteLine($"  next: {hint}");
        return w.Bytes > 0 ? 0 : 1;
    }

    private (string, string, string) Diagnose(Window w, double bps, (string Name, long Bps, bool Loopback)? link, double rttMs)
    {
        bool file = o.Proto == Proto.File;
        string unit = file ? "thread" : "connection";
        if (w.Bytes == 0) return ("NO DATA", "nothing was transferred", "check the errors above");

        if (link is { Loopback: false, Bps: > 0 } l && bps >= 0.9 * l.Bps)
            return ("LINK-BOUND", $"{Fmt.Pct(bps / l.Bps)} of the {Fmt.Bits(l.Bps)} link on {l.Name}",
                    l.Bps < 1_000_000_000 ? "the link negotiated below 1 Gb/s: check the cable, switch port and `ethtool " + l.Name + "`"
                                          : "this is the wire speed; use a faster link or more links (--bind per NIC)");

        bool clientSends = o.Upload;
        double senderTop = clientSends ? w.ClientTop : w.ServerTop ?? 0;
        // (not for TLS: there the saturated threads are busy with crypto, which the checks below attribute correctly)
        if (link is { Loopback: true } && !o.Tls && senderTop >= Saturated)
            return ("LOOPBACK SENDER-BOUND", $"the sending thread ({(clientSends ? "client" : "server")}) uses {Fmt.Pct(senderTop)} of a core; on loopback it also runs the receiver's TCP processing, so this is this host's per-connection ceiling, not a weakness of either side",
                    o.Connections == 1 ? "add connections (-c) to use more cores; measure across a real NIC to compare client and server" : "measure across a real NIC to compare client and server");

        if (w.ClientTop >= Saturated && w.ServerTop is { } both && both >= Saturated)
            return ("CLIENT- AND SERVER-BOUND", $"the busiest thread is saturated on both sides (client {Fmt.Pct(w.ClientTop)}, server {Fmt.Pct(both)})",
                    "add connections (-c) to use more cores on both ends" + (link?.Name.EndsWith("(virtual)") == true ? "; virtual NICs (veth/bridge) cost CPU - use host networking" : ""));

        if (w.ClientTop >= Saturated)
        {
            string hint = o.Connections == 1 ? $"one {unit} is CPU-bound; add more with -c 4 (or more)" : $"add {unit}s (-c) if cores are free";
            if (!file && !o.Upload && !o.RxTrunc && !o.Tls) hint = "use --rx trunc (default) to drop data without copying it; " + hint;
            bool userTls = _st.Any(s => s.TlsInfo?.Contains("user space") == true);
            if (o.Tls) hint = (o.Upload ? "the client encrypts every byte" : "the client decrypts every byte: --tls-rx discard measures the server alone")
                              + (userTls ? "; client TLS runs in user space (load the tls module: sudo modprobe tls)" : "")
                              + (o.Cipher != "aes128" ? "; --cipher aes128 is the cheapest suite with AES-NI" : "") + "; " + hint;
            return ("CLIENT-BOUND", $"the busiest client thread uses {Fmt.Pct(w.ClientTop)} of a core", hint);
        }
        if (w.ServerTop is { } st && st >= Saturated)
            return ("SERVER-BOUND", $"the busiest server thread uses {Fmt.Pct(st)} of a core (client top thread {Fmt.Pct(w.ClientTop)})",
                    (o.Tls ? $"the server {(o.Upload ? "decrypts" : "encrypts")} every byte on that thread; " : "")
                    + (o.Connections == 1 ? "one server thread per connection: add connections (-c)" : "the server needs more cores or is near its limit"));

        // whole-machine CPU: many threads each below one core can still fill every CPU
        int localCpus = Environment.ProcessorCount, serverCpus = _server?.Cpus ?? 0;
        if (link is { Loopback: true } && w.ServerCores is { } both2 && w.ClientCores + both2 >= Saturated * localCpus)
            return ("HOST-CPU-BOUND", $"client and server together use {w.ClientCores + both2:0.0} of the {localCpus} CPUs on this host",
                    "the machine is full: put client and server on separate hosts to see either one's real limit");
        if (w.ClientCores >= Saturated * localCpus)
            return ("CLIENT-BOUND", $"the client uses {w.ClientCores:0.0} of its {localCpus} CPUs", "the client machine is out of CPU: use a bigger client or several clients");
        if (w.ServerCores is { } sc && serverCpus > 0 && sc >= Saturated * serverCpus)
            return ("SERVER-BOUND", $"the server uses {sc:0.0} of its {serverCpus} CPUs", "the server machine is out of CPU" + (o.Tls ? " (TLS: every byte is encrypted or decrypted there)" : ""));

        // TCP window: one connection cannot exceed buffer / RTT
        long bufMax = Net.TcpBufMax(receive: !o.Upload);
        if (!file && rttMs > 0.2 && bufMax > 0)
        {
            double perConnBytes = bps / 8 / o.Connections;
            double inFlight = perConnBytes * rttMs / 1000;
            if (inFlight >= 0.4 * bufMax)
                return ("TCP-WINDOW-BOUND", $"each {unit} keeps {Fmt.Size(inFlight)} in flight at {rttMs:0.##} ms RTT, near the {(o.Upload ? "tcp_wmem" : "tcp_rmem")} max of {Fmt.Size(bufMax)}",
                        $"raise net.ipv4.{(o.Upload ? "tcp_wmem" : "tcp_rmem")} / net.core.{(o.Upload ? "wmem" : "rmem")}_max here (and the opposite one on the server), or add connections");
        }

        if (file)
            return ("LATENCY-BOUND", $"no CPU is saturated; each thread waits for one {Fmt.Size(o.Block)} I/O at a time",
                    "raise -c (more I/O in flight) or --block; for SMB also check the mount's rsize/wsize and multichannel");
        if (link is { Loopback: true })
            return ("NO SINGLE BOTTLENECK", "same host, and no thread on either side is saturated (memory bandwidth or kernel locking)",
                    "add connections; on one host the client and server share cores and memory bandwidth");
        if (w.ServerTop == null)
            return ("NOT THE CLIENT", $"the busiest client thread uses only {Fmt.Pct(w.ClientTop)} of a core; server CPU is unknown",
                    "either the server or the network is slow: run against memserve (with /stats) or check the server's CPU");
        return ("NETWORK-BOUND", $"neither side is CPU-saturated (client {Fmt.Pct(w.ClientTop)}, server {Fmt.Pct(w.ServerTop)}) and the local link is not full",
                "the limit is in the path: switches/links in between, congestion, packet loss (retransmits on the sender), or a slower NIC on the server");
    }

    private void Say(string s) { if (!o.Json) Console.WriteLine(s); }
}

/// <summary>Reads memserve's /stats: bytes moved and CPU (total and per thread) on the server.</summary>
internal sealed class ServerStats
{
    public sealed record Sample(double Bytes, double Cpu, Dictionary<string, double> Threads, long KtlsTx, long TlsUserspace, long Active);
    private readonly HttpClient _http;
    private readonly string _url;
    public int Cpus;

    private ServerStats(string url, bool insecure)
    {
        _url = url;
        var h = new HttpClientHandler();
        if (insecure) h.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        _http = new HttpClient(h) { Timeout = TimeSpan.FromMilliseconds(800) };
    }

    public static ServerStats? Probe(string url, bool insecure, out Sample? first)
    {
        var s = new ServerStats(url, insecure);
        first = s.Take();
        return first != null && first.Cpu > 0 ? s : null;
    }

    public Sample? Take()
    {
        try
        {
            using var doc = JsonDocument.Parse(_http.GetStringAsync(_url).GetAwaiter().GetResult());
            var r = doc.RootElement;
            if (!r.TryGetProperty("uptime_s", out _) || !r.TryGetProperty("cpu_seconds", out var cpu)) return null;
            double bytes = 0;
            long active = 0;
            foreach (var p in r.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("bytes_out", out var bo))
                {
                    bytes += bo.GetDouble() + p.Value.GetProperty("bytes_in").GetDouble();
                    if (p.Value.TryGetProperty("connections_active", out var ca)) active += ca.GetInt64();
                }
            var threads = new Dictionary<string, double>();
            if (r.TryGetProperty("threads", out var th))
                foreach (var t in th.EnumerateObject()) threads[t.Name] = t.Value.GetDouble();
            if (r.TryGetProperty("cpus", out var c)) Cpus = c.GetInt32();
            long ktls = 0, user = 0;
            if (r.TryGetProperty("tls", out var tl))
            {
                ktls = tl.GetProperty("ktls_tx").GetInt64();
                user = tl.GetProperty("userspace").GetInt64();
            }
            return new Sample(bytes, cpu.GetDouble(), threads, ktls, user, active);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
    }
}

internal static class Fmt
{
    public static string Bits(double bps) => bps >= 1e9 ? $"{bps / 1e9:0.00} Gb/s" : bps >= 1e6 ? $"{bps / 1e6:0.0} Mb/s" : $"{bps / 1e3:0.0} kb/s";
    public static string Size(double bytes) => MemServe.Util.Sizes.Human(bytes);
    public static string Pct(double? v) => v is { } x ? $"{x * 100:0}%" : "?";
    public static string HostPart(string host) => host.Contains(':') ? $"[{host}]" : host;
}
