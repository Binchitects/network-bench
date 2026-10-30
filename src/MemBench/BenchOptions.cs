using System.Net;
using MemServe.Util;

namespace MemBench;

public enum Proto { Http, Ftp, File }

public sealed class BenchOptions
{
    public Proto Proto;
    public string Target = "";
    public string Host = "";
    public int Port;
    public string Path = "";
    public string User = "anonymous", Pass = "membench@";

    public int Connections = 1;
    public double Seconds = 10, Interval = 1, Warmup = 1;
    public bool Upload;
    public bool RxTrunc = true;          // download: recv(MSG_TRUNC) - kernel drops the data, no copy to user space
    public bool TxSendfile = true;       // upload: sendfile() from the in-RAM pattern
    public long Buffer = 256 << 10;      // receive buffer per connection (rx=copy, TLS decrypt)
    public long Block = 4 << 20;         // file mode I/O size
    public long BackingSize = 16 << 20;  // upload pattern (small enough to stay in the CPU cache)
    public long FileSize = 10L << 30;    // file write: wrap after this many bytes
    public bool Direct = true, Keep;
    public string? StatsUrl;
    public bool NoStats;
    public IPAddress? Bind;
    public bool Json, Quiet;
    public bool Tls, Insecure, TlsRxDiscard;
    public string? Cipher;               // TLS 1.3 suite: aes128 | aes256 | chacha20
    public string Ktls = "auto";         // client TLS engine: auto = OpenSSL + kernel TLS | off = SslStream

    public static BenchOptions Parse(string[] args)
    {
        var o = new BenchOptions();
        string? target = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "-c": case "--connections": o.Connections = int.Parse(Next()); break;
                case "-t": case "--time": o.Seconds = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "-i": case "--interval": o.Interval = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "-w": case "--warmup": o.Warmup = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "-u": case "--upload": case "--write": o.Upload = true; break;
                case "--rx": o.RxTrunc = Next() switch { "trunc" => true, "copy" => false, var v => throw new ArgumentException($"--rx trunc|copy, not {v}") }; break;
                case "--tx": o.TxSendfile = Next() switch { "sendfile" => true, "copy" => false, var v => throw new ArgumentException($"--tx sendfile|copy, not {v}") }; break;
                case "--buffer": o.Buffer = Sizes.Parse(Next()); break;
                case "--block": o.Block = Sizes.Parse(Next()); break;
                case "--backing": o.BackingSize = Sizes.Parse(Next()); break;
                case "--file-size": o.FileSize = Sizes.Parse(Next()); break;
                case "--buffered": o.Direct = false; break;
                case "--keep": o.Keep = true; break;
                case "--stats": o.StatsUrl = Next(); break;
                case "--no-stats": o.NoStats = true; break;
                case "--bind": o.Bind = IPAddress.Parse(Next()); break;
                case "--user": o.User = Next(); break;
                case "--pass": o.Pass = Next(); break;
                case "--json": o.Json = true; break;
                case "-k": case "--insecure": o.Insecure = true; break;
                case "--ktls": o.Ktls = Next() switch { var v when v is "auto" or "off" => v, var v => throw new ArgumentException($"--ktls auto|off, not {v}") }; break;
                case "--tls-rx": o.TlsRxDiscard = Next() switch { "decrypt" => false, "discard" => true, var v => throw new ArgumentException($"--tls-rx decrypt|discard, not {v}") }; break;
                case "--cipher": o.Cipher = Next() switch { var v when v is "aes128" or "aes256" or "chacha20" => v, var v => throw new ArgumentException($"--cipher aes128|aes256|chacha20, not {v}") }; break;
                case "-q": case "--quiet": o.Quiet = true; break;
                case "-h": case "--help": PrintHelp(); Environment.Exit(0); break;
                default:
                    if (a.StartsWith('-') && a.Length > 1) throw new ArgumentException($"Unknown option {a} (try --help)");
                    if (target != null) throw new ArgumentException($"Only one target allowed (got '{target}' and '{a}')");
                    target = a; break;
            }
        }
        if (target == null) { PrintHelp(); Environment.Exit(2); }
        if (o.Connections < 1) throw new ArgumentException("-c must be >= 1");
        if (o.Seconds <= 0 || o.Interval <= 0 || o.Warmup < 0) throw new ArgumentException("bad -t / -i / -w");
        o.Target = target;

        if (target.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("implicit FTPS (ftps://, port 990) is not supported; use ftpes://host/ for explicit FTPS (AUTH TLS on port 21)");
        if (target.StartsWith("ftpes://", StringComparison.OrdinalIgnoreCase)) { o.Tls = true; target = "ftp://" + target[8..]; }
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var u = new Uri(target);
            o.Tls |= u.Scheme == "https";
            o.Proto = Proto.Http; o.Host = u.Host.Trim('[', ']'); o.Port = u.Port;
            o.Path = u.PathAndQuery is "/" or "" ? (o.Upload ? "/upload" : "/1T") : u.PathAndQuery;
        }
        else if (target.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
        {
            var u = new Uri(target);
            o.Proto = Proto.Ftp; o.Host = u.Host.Trim('[', ']'); o.Port = u.IsDefaultPort ? 21 : u.Port;
            if (u.UserInfo.Length > 0)
            {
                var ui = Uri.UnescapeDataString(u.UserInfo).Split(':', 2);
                o.User = ui[0]; if (ui.Length > 1) o.Pass = ui[1];
            }
            var p = Uri.UnescapeDataString(u.AbsolutePath).TrimStart('/');
            o.Path = p.Length > 0 ? p : (o.Upload ? "membench-upload.bin" : "1T.bin");
        }
        else
        {
            if (o.Tls) throw new ArgumentException("TLS needs an https:// or ftpes:// target");
            o.Proto = Proto.File;
            o.Path = target.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? target[7..] : target;
        }
        if (o.TlsRxDiscard && (!o.Tls || o.Upload)) throw new ArgumentException("--tls-rx discard only applies to TLS downloads");
        return o;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
membench - maximum-throughput test client that also tells you who the bottleneck is

Usage: membench <server>             run every test against a memserve server and write a report
       membench <target> [options]    run one test

  <server>  host name, IP or host:port of memserve (finds its ports, TLS and the SMB share itself)

Targets:
  http://host[:port]/path        HTTP GET (keep-alive, repeated). Default path /1T (memserve).
                                 With -u: PUT of an endless body to /upload.
  https://host[:port]/path       the same over TLS
  ftp://[user:pass@]host/file    FTP RETR (repeated) or, with -u, STOR. Default file 1T.bin.
  ftpes://[user:pass@]host/file  explicit FTPS: AUTH TLS, PROT P (control and data encrypted)
  /path/to/file                  read (or, with -u, write) a file, e.g. on a cifs/NFS mount of
                                 the Samba share, or on Windows Z:\10G.bin / \\server\bench\10G.bin.
                                 Direct I/O by default (Linux O_DIRECT, Windows unbuffered) so the
                                 client cache cannot fake the result.

Options:
  -c, --connections <n>   parallel connections / threads (default 1)
  -t, --time <sec>        measured time (default 10)
  -w, --warmup <sec>      untimed ramp-up before measuring (default 1)
  -i, --interval <sec>    report interval (default 1)
  -u, --upload            upload / write instead of download / read
  --rx trunc|copy         download data path (trunc is Linux-only): trunc = recv(MSG_TRUNC), data is dropped in the
                          kernel without a copy (default, max speed); copy = recv() into a buffer
                          like a normal application
  --tx sendfile|copy      upload data path (default sendfile from a RAM pattern)
  --buffer <size>         receive buffer per connection for --rx copy and TLS (default 256K)
  --backing <size>        RAM pattern that uploads are sent from (default 16M)
  --block <size>          file mode I/O size (default 4M)
  --buffered              file mode without direct I/O (repeated reads may come from the local cache)
  --file-size <size>      file write: wrap around after this many bytes (default 10G)
  --keep                  file write: keep the file afterwards (default: deleted)
  --stats <url>           memserve /stats URL for server-side CPU (default: http://host:port/stats
                          for HTTP targets, http://host/stats for FTP)
  --no-stats              do not query the server
  --bind <ip>             source address (pick the NIC on multi-homed hosts)
  -k, --insecure          TLS: accept any server certificate (memserve's default one is self-signed)
  --cipher <suite>        TLS 1.3 suite: aes128 | aes256 | chacha20 (default: negotiated; on Windows
                          only TLS 1.3 is forced, SChannel picks the suite)
  --ktls auto|off         client TLS engine (Linux): auto = OpenSSL with kernel TLS when the tls module is
                          loaded (decrypts with multi-record recvmsg, uploads with sendfile; default),
                          off = .NET SslStream. --tls-rx discard always uses SslStream.
  --tls-rx decrypt|discard
                          TLS download: decrypt like a real client (default), or drop the encrypted
                          bytes unread with MSG_TRUNC so the client costs ~nothing and only the
                          server's encryption is measured (counts ciphertext bytes)
  --json                  print the final result as JSON
  -q, --quiet             no per-interval lines
""");
    }
}
