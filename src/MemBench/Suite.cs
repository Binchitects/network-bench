using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MemBench;

/// <summary>What the server offers, read from memserve's /stats.</summary>
internal sealed record ServerInfo(string Host, int? Http, int? Https, int? Ftp, bool FtpTls, bool Ktls, string TlsEngine, string Version,
                                  string? SmbShare, int SmbPort, string StatsUrl, int Cpus);

/// <summary>One test case and, after it ran, its result.</summary>
internal sealed class CaseResult(string scenario, string name, string protocol, string direction, int connections, string[] args)
{
    public readonly string Scenario = scenario, Name = name, Protocol = protocol, Direction = direction;
    public readonly int Connections = connections;
    public readonly string[] Args = args;
    public string Status = "PASS";            // PASS | WARN | FAIL | SKIP
    public readonly List<string> Findings = [];
    public double Gbps, ClientCores, ServerCores, Seconds;
    public string? Verdict, Reason, Hint, ServerTls, Link, RawJson;
    public double? LinkGbps;

    public void Warn(string why) { if (Status == "PASS") Status = "WARN"; Findings.Add(why); }
    public void Fail(string why) { Status = "FAIL"; Findings.Add(why); }
}

internal sealed class Scenario(string name, string description)
{
    public readonly string Name = name, Description = description;
    public readonly List<CaseResult> Cases = [];
    public string? SkipReason;
}

/// <summary>
/// `membench SERVER`: finds memserve (and the Samba share next to it), runs the built-in test plan, checks every case
/// and writes the report. Each case runs as its own membench process so no state leaks from one case to the next.
/// </summary>
internal sealed class Suite(string server)
{
    private const int CaseSeconds = 3, CaseWarmup = 1;
    private readonly List<Scenario> _plan = [];
    private ServerInfo _info = null!;
    private string? _smbPath, _smbSkip, _smbFile;

    public int Run()
    {
        var started = DateTime.Now;
        var total = Stopwatch.StartNew();
        Console.WriteLine($"membench test run -> {server}");
        var info = Discover();
        if (info == null)
        {
            Console.WriteLine($"  no memserve found at {server} (tried HTTP 80, 8080, 8088 and HTTPS 443, 8443; give host:port if it uses another port)");
            return 2;
        }
        _info = info;
        Console.WriteLine($"  server : memserve {info.Version}, {info.Cpus} cpus; " + string.Join(", ", new[]
        {
            info.Http is { } h ? $"HTTP {h}" : null,
            info.Https is { } s ? $"HTTPS {s} ({(info.Ktls ? "kernel TLS" : info.TlsEngine == "none" ? "no TLS" : "user-space TLS")})" : null,
            info.Ftp is { } f ? $"FTP {f}{(info.FtpTls ? " + FTPS" : "")}" : null,
        }.Where(x => x != null)));
        FindSmb();
        Console.WriteLine($"  smb    : {(_smbPath != null ? $"{_smbFile} on {_smbPath}" : "skipped - " + _smbSkip)}");
        Console.WriteLine($"  client : {RuntimeInformation.OSDescription.Trim()}, {Environment.ProcessorCount} cpus, membench {typeof(Suite).Assembly.GetName().Version?.ToString(3)}");
        if (MemServe.Util.CpuInfo.HybridNote() is { } hy) Console.WriteLine($"  note   : {hy}");

        BuildPlan();
        int n = _plan.Sum(s => s.Cases.Count), i = 0;
        Console.WriteLine($"  plan   : {_plan.Count} scenarios, {n} test cases, about {TimeSpan.FromSeconds(n * (CaseSeconds + CaseWarmup + 1.5)):m\\:ss} min");

        foreach (var sc in _plan)
        {
            Console.WriteLine();
            Console.WriteLine($"{sc.Name} - {sc.Description}");
            if (sc.SkipReason != null) Console.WriteLine($"  skipped: {sc.SkipReason}");
            foreach (var c in sc.Cases)
            {
                i++;
                string label = $"  [{i,2}/{n}] {c.Name} ";
                Console.Write(label.PadRight(58, '.') + " ");
                if (sc.SkipReason != null) { c.Status = "SKIP"; c.Findings.Add(sc.SkipReason); }
                else RunCase(c);
                Console.WriteLine(c.Status == "SKIP" ? "            SKIP" : $"{c.Gbps,9:0.00} Gb/s  {c.Status,-4}  {c.Verdict}");
                foreach (var finding in c.Findings.Where(_ => c.Status is "WARN" or "FAIL")) Console.WriteLine($"          {c.Status.ToLowerInvariant()}: {finding}");
            }
            CheckScaling(sc);
        }

        var dir = Report.Write(_plan, _info, server, started, total.Elapsed, _smbPath != null ? $"{_smbFile} on {_smbPath}" : "skipped - " + _smbSkip);
        int pass = Count("PASS"), warn = Count("WARN"), fail = Count("FAIL"), skip = Count("SKIP");
        Console.WriteLine();
        Console.WriteLine($"Summary: {pass} passed, {warn} warnings, {fail} failed, {skip} skipped in {total.Elapsed:m\\:ss}");
        foreach (var sc in _plan.Where(s => s.Cases.Any(c => c.Gbps > 0)))
        {
            var best = sc.Cases.MaxBy(c => c.Gbps)!;
            Console.WriteLine($"  {sc.Name,-34} best {best.Gbps,8:0.00} Gb/s  ({best.Name})");
        }
        Console.WriteLine($"Report: {Path.Combine(dir, "report.html")}  (also report.md, results.csv, results.json)");
        return fail > 0 ? 1 : 0;
    }

    private int Count(string status) => _plan.Sum(s => s.Cases.Count(c => c.Status == status));

    // ---------------------------------------------------------------- discovery

    private ServerInfo? Discover()
    {
        // "host", "host:port", "[v6]:port"
        string host = server;
        int? port = null;
        if (Uri.TryCreate("tcp://" + server, UriKind.Absolute, out var parsed))
        {
            host = parsed.Host.Trim('[', ']');
            if (parsed.Port > 0) port = parsed.Port;
        }
        string hp = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        var candidates = port is { } only
            ? new[] { $"http://{hp}:{only}/stats", $"https://{hp}:{only}/stats" }
            : new[] { $"http://{hp}:80/stats", $"http://{hp}:8088/stats", $"http://{hp}:8080/stats", $"https://{hp}:443/stats", $"https://{hp}:8443/stats" };
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator })
            { Timeout = TimeSpan.FromSeconds(3) };
        var probes = candidates.Select(u => (Url: u, Task: http.GetStringAsync(u))).ToArray();
        foreach (var (url, task) in probes)
        {
            try
            {
                using var doc = JsonDocument.Parse(task.GetAwaiter().GetResult());
                var r = doc.RootElement;
                if (!r.TryGetProperty("uptime_s", out _)) continue;
                int cpus = r.TryGetProperty("cpus", out var c) ? c.GetInt32() : 0;
                var u = new Uri(url);
                host = host.Trim('[', ']');
                if (!r.TryGetProperty("server", out var s))
                    // older memserve: only the port that answered is known
                    return new ServerInfo(host, u.Scheme == "http" ? u.Port : null, u.Scheme == "https" ? u.Port : null, null, false, false, "unknown", "?", null, 445, url, cpus);
                int? Port(string k) => s.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
                bool Flag(string k) => s.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
                string? smb = s.TryGetProperty("smb", out var sm) && sm.ValueKind == JsonValueKind.Object ? sm.GetProperty("share").GetString() : null;
                int smbPort = smb != null ? sm.GetProperty("port").GetInt32() : 445;
                // prefer plain HTTP for /stats: it costs nothing and works with any client
                string stats = Port("http") is { } h ? $"http://{hp}:{h}/stats" : url;
                return new ServerInfo(host, Port("http"), Port("https"), Port("ftp"), Flag("ftp_tls"), Flag("ktls"),
                    s.GetProperty("tls_engine").GetString() ?? "?", s.GetProperty("version").GetString() ?? "?", smb, smbPort, stats, cpus);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) { }
        }
        return null;
    }

    /// <summary>SMB needs the OS client: a UNC path on Windows, an existing cifs mount on Linux.</summary>
    private void FindSmb()
    {
        string share = _info.SmbShare ?? "bench";
        if (!PortOpen(_info.Host, _info.SmbPort))
        {
            _smbSkip = $"no SMB server on {_info.Host}:{_info.SmbPort}";
            return;
        }
        string? dir = null;
        if (OperatingSystem.IsWindows())
        {
            string unc = $@"\\{_info.Host}\{share}";
            dir = WithTimeout(() => Directory.Exists(unc), 8) ? unc : null;
            if (dir == null) { _smbSkip = $@"cannot open {unc}; connect it first: net use {unc} /user:bench bench"; return; }
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                foreach (var line in File.ReadLines("/proc/mounts"))
                {
                    var f = line.Split(' ');
                    if (f.Length < 3 || f[2] is not ("cifs" or "smb3")) continue;
                    var src = f[0].TrimStart('/').Split('/');
                    if (src.Length >= 2 && src[0].Equals(_info.Host, StringComparison.OrdinalIgnoreCase)) { dir = f[1].Replace("\\040", " "); break; }
                }
            }
            catch (IOException) { }
            if (dir == null)
            {
                _smbSkip = $"//{_info.Host}/{share} is not mounted; mount it first: sudo mount -t cifs //{_info.Host}/{share} /mnt/smb -o username=bench,password=bench,multichannel";
                return;
            }
        }
        else { _smbSkip = "SMB tests need Linux (cifs mount) or Windows"; return; }

        var file = WithTimeout(() => new DirectoryInfo(dir).GetFiles("*.bin").OrderByDescending(x => x.Length).FirstOrDefault(), 8);
        if (file == null) { _smbSkip = $"no .bin test files on {dir}"; return; }
        _smbPath = dir;
        _smbFile = file.Name;
    }

    private static bool PortOpen(string host, int port)
    {
        try
        {
            using var s = new TcpClient();
            return s.ConnectAsync(host, port).Wait(2000) && s.Connected;
        }
        catch (Exception e) when (e is SocketException or AggregateException) { return false; }
    }

    private static T? WithTimeout<T>(Func<T> f, int seconds)
    {
        var t = Task.Run(f);
        try { return t.Wait(TimeSpan.FromSeconds(seconds)) ? t.Result : default; }
        catch (AggregateException) { return default; }
    }

    // ---------------------------------------------------------------- the test plan

    private void BuildPlan()
    {
        var i = _info;
        string? http = i.Http is { } hp ? $"http://{Url(i.Host)}:{hp}/" : null;
        string? https = i.Https is { } sp ? $"https://{Url(i.Host)}:{sp}/" : null;
        string? ftp = i.Ftp is { } fp ? $"ftp://{Url(i.Host)}:{fp}/" : null;
        string? ftpes = i.Ftp is { } fp2 && i.FtpTls ? $"ftpes://{Url(i.Host)}:{fp2}/" : null;
        string[] stats = ["--stats", i.StatsUrl];

        Scenario S(string name, string desc) { var s = new Scenario(name, desc); _plan.Add(s); return s; }
        void Add(Scenario s, string? target, string name, string proto, string dir, int conns, params string[] extra)
        {
            var args = target == null ? [] : new[] { target, "-c", conns.ToString() }.Concat(extra).Concat(proto.StartsWith("FTP") || proto == "SMB" ? stats : []).ToArray();
            var c = new CaseResult(s.Name, name, proto, dir, conns, args);
            if (target == null) { c.Status = "SKIP"; c.Findings.Add($"{proto} is not enabled on the server"); }
            s.Cases.Add(c);
        }
        string[] up = ["-u"], k = ["-k"];

        var single = S("Single connection", "what one TCP or TLS stream can carry");
        Add(single, http, "HTTP download", "HTTP", "download", 1);
        Add(single, http, "HTTP upload", "HTTP", "upload", 1, up);
        Add(single, https, "HTTPS download", "HTTPS", "download", 1, k);
        Add(single, https, "HTTPS upload", "HTTPS", "upload", 1, [.. k, .. up]);
        Add(single, ftp, "FTP download (RETR)", "FTP", "download", 1);
        Add(single, ftp, "FTP upload (STOR)", "FTP", "upload", 1, up);
        Add(single, ftpes, "FTPS download (RETR)", "FTPS", "download", 1, k);
        Add(single, ftpes, "FTPS upload (STOR)", "FTPS", "upload", 1, [.. k, .. up]);

        foreach (var (title, target, proto, extra) in new[]
        {
            ("Scaling: HTTP download", http, "HTTP", Array.Empty<string>()),
            ("Scaling: HTTP upload", http, "HTTP", up),
            ("Scaling: HTTPS download", https, "HTTPS", k),
            ("Scaling: HTTPS upload", https, "HTTPS", [.. k, .. up]),
        })
        {
            var s = S(title, "the same transfer spread over more parallel connections");
            foreach (int c in new[] { 4, 16, 64 })
                Add(s, target, $"{proto} {(extra.Contains("-u") ? "upload" : "download")}, {c} connections", proto, extra.Contains("-u") ? "upload" : "download", c, extra);
        }

        var tls = S("TLS cost", "what encryption costs, 8 connections each");
        Add(tls, https, "server only (ciphertext dropped unread)", "HTTPS", "download", 8, [.. k, "--tls-rx", "discard"]);
        Add(tls, https, "AES-128-GCM, client decrypts", "HTTPS", "download", 8, [.. k, "--cipher", "aes128"]);
        Add(tls, https, "AES-256-GCM, client decrypts", "HTTPS", "download", 8, [.. k, "--cipher", "aes256"]);
        Add(tls, https, "ChaCha20-Poly1305, client decrypts", "HTTPS", "download", 8, [.. k, "--cipher", "chacha20"]);
        if (Platform.Linux)
            Add(tls, https, "client user-space TLS (no kernel TLS)", "HTTPS", "download", 8, [.. k, "--ktls", "off"]);

        var ftps = S("FTP sessions", "parallel FTP and FTPS transfers, 8 sessions each");
        Add(ftps, ftp, "FTP download, 8 sessions", "FTP", "download", 8);
        Add(ftps, ftp, "FTP upload, 8 sessions", "FTP", "upload", 8, up);
        Add(ftps, ftpes, "FTPS download, 8 sessions", "FTPS", "download", 8, k);

        var many = S("Many connections", "fairness and stability with hundreds of connections");
        Add(many, http, "HTTP download, 256 connections", "HTTP", "download", 256);
        Add(many, https, "HTTPS download, 128 connections", "HTTPS", "download", 128, k);

        var smb = S("SMB share", "reading and writing a file on the Samba share through the OS client");
        string? read = _smbPath != null ? Path.Combine(_smbPath, _smbFile!) : null;
        string? write = _smbPath != null ? Path.Combine(_smbPath, $"membench-{Environment.MachineName}.tmp") : null;
        Add(smb, read, "SMB read, 1 thread", "SMB", "download", 1);
        Add(smb, read, "SMB read, 8 threads", "SMB", "download", 8);
        Add(smb, write, "SMB write, 4 threads", "SMB", "upload", 4, [.. up, "--file-size", "1G"]);
        if (_smbPath == null) smb.SkipReason = _smbSkip;
    }

    private static string Url(string host) => host.Contains(':') ? $"[{host}]" : host;

    // ---------------------------------------------------------------- running and checking

    private void RunCase(CaseResult c)
    {
        if (c.Status == "SKIP") return;
        var (exe, prefix) = Self();
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in prefix.Concat(c.Args).Concat(["--json", "-q", "-t", $"{CaseSeconds}", "-w", $"{CaseWarmup}"])) psi.ArgumentList.Add(a);
        string output;
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromSeconds(CaseSeconds + CaseWarmup + 45)))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                c.Fail("timed out");
                return;
            }
            output = stdout.Result + stderr.Result;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { c.Fail($"cannot start membench: {e.Message}"); return; }

        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        if (line == null) { c.Fail(output.Trim().Split('\n').LastOrDefault()?.Trim() is { Length: > 0 } last ? last : "no result"); return; }
        c.RawJson = line;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            double D(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            c.Gbps = D("gbps"); c.ClientCores = D("client_cores"); c.ServerCores = D("server_cores"); c.Seconds = D("seconds");
            c.Verdict = S("verdict"); c.Reason = S("reason"); c.Hint = S("hint"); c.ServerTls = S("server_tls"); c.Link = S("link");
            c.LinkGbps = r.TryGetProperty("link_gbps", out var lg) && lg.ValueKind == JsonValueKind.Number ? lg.GetDouble() : null;
            var errors = r.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToArray();
            var per = r.GetProperty("per_connection_gbps").EnumerateArray().Select(e => e.GetDouble()).ToArray();

            if (errors.Length > 0) c.Fail("error: " + errors[0]);
            if (D("bytes") <= 0) c.Fail("no data was transferred");
            if (c.Status == "FAIL") return;
            if (per.Length > 1)
            {
                double avg = per.Average();
                int starved = per.Count(x => x < 0.1 * avg);
                if (starved > 0) c.Warn($"{starved} of {per.Length} connections got under 10% of the average rate (unfair sharing)");
            }
            if (c.Protocol is "HTTPS" or "FTPS" && c.ServerTls?.StartsWith("user space") == true)
                c.Warn("the server encrypts in user space; `sudo modprobe tls` on the server enables kernel TLS (+50% or more)");
            if (c.LinkGbps is { } link && c.Link != "lo" && c.Gbps < 0.5 && link >= 1)
                c.Warn($"only {c.Gbps:0.00} Gb/s on a {link:0.#} Gb/s link");
            if (c.Verdict == "LINK-BOUND" && c.LinkGbps is < 1)
                c.Warn($"the link negotiated only {c.LinkGbps * 1000:0} Mb/s: check the cable and switch port");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { c.Fail("unreadable result: " + e.Message); }
    }

    /// <summary>Adding connections should not lower the total: flag the largest case when it falls well below the best.</summary>
    private static void CheckScaling(Scenario sc)
    {
        if (!sc.Name.StartsWith("Scaling")) return;
        var ran = sc.Cases.Where(c => c.Status is "PASS" or "WARN" && c.Gbps > 0).ToList();
        if (ran.Count < 2) return;
        var best = ran.MaxBy(c => c.Gbps)!;
        var last = ran.MaxBy(c => c.Connections)!;
        if (last != best && last.Gbps < 0.8 * best.Gbps)
        {
            last.Warn($"{last.Connections} connections give {last.Gbps / best.Gbps:P0} of the best ({best.Gbps:0.0} Gb/s with {best.Connections})");
            Console.WriteLine($"          warn: {last.Findings[^1]}");
        }
    }

    /// <summary>How to start another membench: the same executable (or `dotnet membench.dll`).</summary>
    private static (string Exe, string[] Prefix) Self()
    {
        string exe = Environment.ProcessPath ?? "membench";
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return (exe, [typeof(Suite).Assembly.Location]);
        return (exe, []);
    }
}
