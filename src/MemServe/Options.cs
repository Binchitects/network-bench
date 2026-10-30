using System.Net;
using MemServe.Util;

namespace MemServe;

public sealed class Options
{
    public IPAddress Bind = IPAddress.IPv6Any;   // dual-stack by default
    public int HttpPort = 80;
    public int HttpsPort = 443;
    public int FtpPort = 21;
    public string? TlsCert, TlsKey;  // PEM; without them a self-signed certificate is generated
    public bool FtpTls = true;       // allow AUTH TLS (explicit FTPS) on the FTP port
    public string Ktls = "auto";     // auto: OpenSSL + kernel TLS when available | zerocopy | off (SslStream)
    public long BackingSize = 1L << 30;
    public List<string> Files = ["1M", "10M", "100M", "1G", "10G", "100G", "1T"];
    public int FtpPasvMin = 50000, FtpPasvMax = 50100;
    public IPAddress? FtpPasvAddr;
    public int StatsInterval = 1;
    // advertised in /stats so membench can find the Samba share next to this server (memserve does not serve SMB)
    public string? SmbShare;
    public int SmbPort = 445;
    public bool Verbose, Quiet;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        // environment first (for docker), CLI overrides
        // empty values count as unset so compose can pass "${VAR:-}" through
        string? E(string k) => Environment.GetEnvironmentVariable("MEMSERVE_" + k) is { Length: > 0 } v ? v : null;
        if (E("BIND") is { } b) o.Bind = IPAddress.Parse(b);
        if (E("HTTP_PORT") is { } hp) o.HttpPort = int.Parse(hp);
        if (E("FTP_PORT") is { } fp) o.FtpPort = int.Parse(fp);
        if (E("HTTPS_PORT") is { } tp) o.HttpsPort = int.Parse(tp);
        if (E("TLS_CERT") is { } tc) o.TlsCert = tc;
        if (E("TLS_KEY") is { } tk) o.TlsKey = tk;
        if (E("FTP_TLS") is { } ft) o.FtpTls = IsTrue(ft);
        if (E("KTLS") is { } kt) o.Ktls = CheckKtls(kt);
        if (E("BACKING") is { } bs) o.BackingSize = Sizes.Parse(bs);
        if (E("FILES") is { } fl) o.Files = SplitList(fl);
        if (E("FTP_PASV_PORTS") is { } pp) ParseRange(pp, out o.FtpPasvMin, out o.FtpPasvMax);
        if (E("FTP_PASV_ADDR") is { } pa) o.FtpPasvAddr = IPAddress.Parse(pa);
        if (E("STATS_INTERVAL") is { } si) o.StatsInterval = int.Parse(si);
        if (E("SMB_SHARE") is { } ss) o.SmbShare = ss;
        if (E("SMB_PORT") is { } sp) o.SmbPort = int.Parse(sp);
        if (E("VERBOSE") is { } v) o.Verbose = IsTrue(v);
        if (E("QUIET") is { } q) o.Quiet = IsTrue(q);

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "--bind": o.Bind = IPAddress.Parse(Next()); break;
                case "--http-port": o.HttpPort = int.Parse(Next()); break;
                case "--ftp-port": o.FtpPort = int.Parse(Next()); break;
                case "--no-http": o.HttpPort = 0; break;
                case "--https-port": o.HttpsPort = int.Parse(Next()); break;
                case "--no-https": o.HttpsPort = 0; break;
                case "--tls-cert": o.TlsCert = Next(); break;
                case "--tls-key": o.TlsKey = Next(); break;
                case "--ftp-tls": o.FtpTls = IsTrue(Next()); break;
                case "--ktls": o.Ktls = CheckKtls(Next()); break;
                case "--no-ftp": o.FtpPort = 0; break;
                case "--backing": o.BackingSize = Sizes.Parse(Next()); break;
                case "--files": o.Files = SplitList(Next()); break;
                case "--ftp-pasv-ports": ParseRange(Next(), out o.FtpPasvMin, out o.FtpPasvMax); break;
                case "--ftp-pasv-addr": o.FtpPasvAddr = IPAddress.Parse(Next()); break;
                case "--stats-interval": o.StatsInterval = int.Parse(Next()); break;
                case "--smb-share": o.SmbShare = Next(); break;
                case "--smb-port": o.SmbPort = int.Parse(Next()); break;
                case "-v": case "--verbose": o.Verbose = true; break;
                case "-q": case "--quiet": o.Quiet = true; break;
                case "-h": case "--help": PrintHelp(); Environment.Exit(0); break;
                default: throw new ArgumentException($"Unknown argument: {a} (try --help)");
            }
        }
        return o;
    }

    private static string CheckKtls(string v) => v is "auto" or "zerocopy" or "off" ? v : throw new ArgumentException($"--ktls auto|zerocopy|off, not {v}");

    private static bool IsTrue(string s) => s is "1" or "true" or "yes" or "on" or "True" or "TRUE" or "YES" or "ON";
    private static List<string> SplitList(string s) => s.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    private static void ParseRange(string s, out int lo, out int hi)
    {
        var p = s.Split('-');
        lo = int.Parse(p[0]); hi = p.Length > 1 ? int.Parse(p[1]) : lo;
        if (hi < lo) (lo, hi) = (hi, lo);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
memserve - in-memory, zero-copy HTTP/FTP file server for network throughput testing

Usage: memserve [options]           (every option also exists as env var MEMSERVE_<NAME>, e.g. MEMSERVE_HTTP_PORT)

  --bind <ip>                 address to listen on (default :: = all IPv4+IPv6)
  --http-port <n>             HTTP port (default 80, 0 = off)        --no-http
  --https-port <n>            HTTPS port (default 443, 0 = off)      --no-https
  --ftp-port <n>              FTP port (default 21, 0 = off)         --no-ftp
  --ftp-tls <bool>            allow explicit FTPS (AUTH TLS) on the FTP port (default true)
  --tls-cert <pem> / --tls-key <pem>
                              certificate and key for HTTPS/FTPS (default: self-signed ECDSA, made at start)
  --ktls auto|zerocopy|off    TLS engine: auto = OpenSSL, kernel TLS when the tls module is loaded (sendfile
                              stays zero-copy); zerocopy = also kTLS TX zerocopy sendfile; off = .NET SslStream
  --backing <size>            RAM used for the random data pattern (default 1G). Files of ANY size
                              are served by cycling through this pattern; nothing is stored on disk.
  --files <list>              virtual files to list (default 1M,10M,100M,1G,10G,100G,1T -> 1G.bin ...)
                              any other <size>.bin (e.g. 25G.bin, 1.5T.bin) also works on demand.
  --ftp-pasv-ports <a-b>      passive data port range (default 50000-50100)
  --ftp-pasv-addr <ip>        public IP to advertise in PASV replies (NAT / bridged docker)
  --stats-interval <sec>      throughput line every N seconds (default 1, 0 = off)
  --smb-share <name>          name of a Samba share on this host to advertise in /stats (membench tests it)
  --smb-port <n>              its port (default 445)
  -v, --verbose               protocol-level logging        -q, --quiet   only errors

Uploads (HTTP PUT/POST, FTP STOR) are counted and discarded without copying (Linux).
""");
    }
}
