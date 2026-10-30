using System.Net.Sockets;
using MemServe;
using MemServe.Core;
using MemServe.Util;

Options opt;
try { opt = Options.Parse(args); }
catch (Exception e) { Console.Error.WriteLine(e.Message); return 2; }
Log.Verbose = opt.Verbose; Log.Quiet = opt.Quiet;

Console.WriteLine($"memserve {typeof(Options).Assembly.GetName().Version?.ToString(3)} - in-memory HTTP/FTP benchmark file server ({(OperatingSystem.IsLinux() ? "linux" : Environment.OSVersion.Platform.ToString())}, {Environment.ProcessorCount} cpus)");
var sw = System.Diagnostics.Stopwatch.StartNew();
Backing backing;
try { backing = Backing.Create(opt.BackingSize); }
catch (Exception e) { Console.Error.WriteLine($"cannot allocate backing store: {e.Message}"); return 1; }
var fs = new VirtualFs(backing, opt.Files);
var stats = new Stats();
Console.WriteLine($"  pattern : {Sizes.Human(backing.Size)} random data in RAM, filled in {sw.ElapsedMilliseconds} ms, {(backing.ZeroCopy ? "memfd + sendfile out, MSG_TRUNC in: zero-copy" : "copy mode")}");
Console.WriteLine($"  files   : {string.Join(' ', fs.List().Select(e => e.Name))}  (+ any <size>.bin on demand)");

Tls? tls = null;
if (opt.HttpsPort > 0 || (opt.FtpPort > 0 && opt.FtpTls))
{
    try
    {
        tls = Tls.Create(opt.TlsCert, opt.TlsKey, opt.Ktls);
        Console.WriteLine($"  tls     : {tls.Describe()}");
    }
    catch (Exception e) { Console.Error.WriteLine($"  tls     : disabled, cannot load certificate: {e.Message}"); }
}

if (CpuInfo.HybridNote() is { } cpuNote) Console.WriteLine($"  cpu     : {cpuNote}");

int started = 0;
bool Try(string what, int port, Action start, string url)
{
    if (port <= 0) { Console.WriteLine($"  {what,-8}: disabled"); return false; }
    try { start(); started++; Console.WriteLine($"  {what,-8}: {url}"); return true; }
    catch (SocketException e) when (e.SocketErrorCode == SocketError.AccessDenied)
    {
        Console.Error.WriteLine($"  {what,-8}: cannot bind port {port} (permission). Run as root, use --{what.ToLowerInvariant()}-port <1024+>, or: sudo setcap cap_net_bind_service=+ep $(readlink -f $(command -v memserve))");
    }
    catch (SocketException e) { Console.Error.WriteLine($"  {what,-8}: cannot bind port {port}: {e.Message}"); }
    return false;
}

string host = opt.Bind.Equals(System.Net.IPAddress.IPv6Any) || opt.Bind.Equals(System.Net.IPAddress.Any) ? "<host>" : opt.Bind.ToString();
bool httpOn = Try("HTTP", opt.HttpPort, () => new MemServe.Http.HttpServer(opt, fs, stats).Start(), $"http://{host}{(opt.HttpPort == 80 ? "" : ":" + opt.HttpPort)}/");
bool httpsOn = tls != null && Try("HTTPS", opt.HttpsPort, () => new MemServe.Http.HttpServer(opt, fs, stats, tls).Start(), $"https://{host}{(opt.HttpsPort == 443 ? "" : ":" + opt.HttpsPort)}/");
bool ftpOn = Try("FTP", opt.FtpPort, () => new MemServe.Ftp.FtpServer(opt, fs, stats, opt.FtpTls ? tls : null).Start(), $"ftp://{host}{(opt.FtpPort == 21 ? "" : ":" + opt.FtpPort)}/  (any user/password, passive ports {opt.FtpPasvMin}-{opt.FtpPasvMax}{(opt.FtpTls && tls != null ? ", AUTH TLS" : "")})");
if (started == 0) { Console.Error.WriteLine("nothing is listening; exiting"); return 1; }

// what membench needs to find everything from one address
static string P(bool on, int port) => on ? port.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
stats.ServerInfo = $"{{\"version\":\"{typeof(Options).Assembly.GetName().Version?.ToString(3)}\",\"http\":{P(httpOn, opt.HttpPort)},\"https\":{P(httpsOn, opt.HttpsPort)}," +
                   $"\"ftp\":{P(ftpOn, opt.FtpPort)},\"ftp_tls\":{(ftpOn && opt.FtpTls && tls != null ? "true" : "false")}," +
                   $"\"tls_engine\":\"{tls?.Engine ?? "none"}\",\"ktls\":{(tls?.Engine == "openssl" && Tls.KernelTlsLoaded() ? "true" : "false")}," +
                   $"\"smb\":{(opt.SmbShare is { } share ? $"{{\"share\":\"{share}\",\"port\":{opt.SmbPort}}}" : "null")}}}";

stats.StartReporter(opt.StatsInterval);
var done = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => done.Set();
done.Wait();
Console.WriteLine("bye");
return 0;
