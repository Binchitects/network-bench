using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MemServe.Core;
using MemServe.Util;

namespace MemServe.Ftp;

/// <summary>
/// FTP server (RFC 959/2428/3659): passive + active modes, RETR with REST (zero-copy sendfile), STOR sink, LIST/NLST/MLSD/MLST/SIZE.
/// Explicit FTPS (RFC 4217: AUTH TLS, PBSZ, PROT P) when tls is given. Single flat directory, any user/password accepted.
/// </summary>
public sealed class FtpServer(Options opt, VirtualFs fs, Stats stats, Tls? tls = null)
{
    internal readonly Tls? Tls = tls;
    internal readonly Options Opt = opt;
    internal readonly VirtualFs Fs = fs;
    private readonly ProtoStats _st = stats.Ftp;
    private int _nextPasvPort = opt.FtpPasvMin;

    public void Start()
    {
        var l = Wire.Listen(opt.Bind, opt.FtpPort);
        Wire.AcceptLoop(l, "ftp", s => new Session(this, s).Run());
    }

    private int NextPasvPort()
    {
        int p = Interlocked.Increment(ref _nextPasvPort);
        if (p > opt.FtpPasvMax) { Interlocked.Exchange(ref _nextPasvPort, opt.FtpPasvMin); p = opt.FtpPasvMin; }
        return p;
    }

    private sealed class Session(FtpServer srv, Socket ctl)
    {
        private readonly Options opt = srv.Opt;
        private readonly VirtualFs fs = srv.Fs;
        private readonly ProtoStats st = srv._st;
        private readonly byte[] _rx = new byte[4096];
        private int _rxLen;
        private bool _binary = true;
        private long _rest;
        private Socket? _pasvListener;
        private IPEndPoint? _activeTarget;
        private string? _renameFrom;
        private readonly StringBuilder _sb = new();
        private Conn _c = Conn.Plain(ctl);   // becomes TLS after AUTH TLS
        private bool _protP;                 // PROT P: data connections use TLS too

        public void Run()
        {
            st.Connected();
            try
            {
                ctl.ReceiveTimeout = 600_000;
                Reply(220, "memserve FTP ready (in-memory benchmark server)");
                while (true)
                {
                    string? line = ReadLine();
                    if (line == null) return;
                    Log.Debug($"ftp {Wire.SafeRemote(ctl)} <- {line}");
                    int sp = line.IndexOf(' ');
                    string cmd = (sp < 0 ? line : line[..sp]).ToUpperInvariant();
                    string arg = sp < 0 ? "" : line[(sp + 1)..].Trim();
                    if (!Handle(cmd, arg)) return;
                }
            }
            finally
            {
                _pasvListener?.Close();
                _c.Dispose();
                st.Disconnected();
            }
        }

        private string? ReadLine()
        {
            while (true)
            {
                int idx = Array.IndexOf(_rx, (byte)'\n', 0, _rxLen);
                if (idx >= 0)
                {
                    int end = idx > 0 && _rx[idx - 1] == '\r' ? idx - 1 : idx;
                    // strip Telnet IAC sequences some clients send before ABOR
                    int start = 0;
                    while (start < end && _rx[start] == 0xFF) start += 2;
                    string s = Encoding.UTF8.GetString(_rx, Math.Min(start, end), Math.Max(0, end - Math.Min(start, end)));
                    _rxLen -= idx + 1;
                    Buffer.BlockCopy(_rx, idx + 1, _rx, 0, _rxLen);
                    return s;
                }
                if (_rxLen == _rx.Length) { Reply(500, "Line too long"); return null; }
                int n;
                try { n = _c.Receive(_rx.AsSpan(_rxLen)); }
                catch (SocketException) { return null; }
                if (n <= 0) return null;
                _rxLen += n;
            }
        }

        private void Reply(int code, string text)
        {
            Log.Debug($"ftp -> {code} {text}");
            _c.SendAll(Encoding.UTF8.GetBytes($"{code} {text}\r\n"));
        }

        private void ReplyMulti(int code, string first, IEnumerable<string> lines, string last)
        {
            _sb.Clear();
            _sb.Append(code).Append('-').Append(first).Append("\r\n");
            foreach (var l in lines) _sb.Append(' ').Append(l).Append("\r\n");
            _sb.Append(code).Append(' ').Append(last).Append("\r\n");
            _c.SendAll(Encoding.UTF8.GetBytes(_sb.ToString()));
        }

        private bool Handle(string cmd, string arg)
        {
            switch (cmd)
            {
                case "USER": Reply(331, "Any password is fine"); return true;
                case "PASS": Reply(230, "Logged in. Files are generated in RAM; uploads are discarded."); return true;
                case "ACCT": Reply(230, "OK"); return true;
                case "QUIT": Reply(221, "Goodbye"); return false;
                case "NOOP": Reply(200, "OK"); return true;
                case "SYST": Reply(215, "UNIX Type: L8"); return true;
                case "FEAT":
                    ReplyMulti(211, "Features:", ["UTF8", "SIZE", "MDTM", "REST STREAM", "MLST type*;size*;modify*;perm*;", "MLSD", "EPSV", "EPRT", "PASV", "TVFS",
                        .. (srv.Tls != null ? (string[])["AUTH TLS", "PBSZ", "PROT"] : [])], "End");
                    return true;
                case "OPTS": Reply(arg.StartsWith("UTF8", StringComparison.OrdinalIgnoreCase) ? 200 : 501, arg.StartsWith("UTF8", StringComparison.OrdinalIgnoreCase) ? "UTF8 set to on" : "Option not understood"); return true;
                case "HELP": Reply(214, "USER PASS QUIT NOOP SYST FEAT OPTS PWD CWD CDUP TYPE MODE STRU PASV EPSV PORT EPRT LIST NLST MLSD MLST SIZE MDTM REST RETR STOR APPE DELE RNFR RNTO STAT ABOR AUTH PBSZ PROT"); return true;
                case "PWD": case "XPWD": Reply(257, "\"/\" is the current directory"); return true;
                case "CWD": case "XCWD":
                    if (IsRoot(arg)) Reply(250, "Directory changed to /"); else Reply(550, "No such directory (this server has a single flat directory)");
                    return true;
                case "CDUP": case "XCUP": Reply(250, "Directory changed to /"); return true;
                case "TYPE":
                    _binary = !arg.StartsWith("A", StringComparison.OrdinalIgnoreCase);
                    Reply(200, _binary ? "Type set to I" : "Type set to A (data is binary anyway)"); return true;
                case "MODE": Reply(arg.Equals("S", StringComparison.OrdinalIgnoreCase) ? 200 : 504, "Mode S"); return true;
                case "STRU": Reply(arg.Equals("F", StringComparison.OrdinalIgnoreCase) ? 200 : 504, "Structure F"); return true;
                case "ALLO": Reply(202, "No storage allocation necessary"); return true;
                case "PASV": return Pasv(extended: false);
                case "EPSV":
                    if (arg.Equals("ALL", StringComparison.OrdinalIgnoreCase)) { Reply(200, "EPSV ALL ok"); return true; }
                    return Pasv(extended: true);
                case "PORT": return Port(arg);
                case "EPRT": return Eprt(arg);
                case "LIST": return SendListing(StripOptions(arg), Listing.Ls);
                case "NLST": return SendListing(StripOptions(arg), Listing.Names);
                case "MLSD": return SendListing(arg, Listing.Mlsd);
                case "MLST":
                    if (IsRoot(arg)) { ReplyMulti(250, "Listing /", [$"type=dir;modify={Ts(fs.StartTime)};perm=el; /"], "End"); return true; }
                    if (fs.TryGet(arg, out var me)) { ReplyMulti(250, "Listing " + me.Name, [MlsxFact(me, true)], "End"); return true; }
                    Reply(550, "No such file"); return true;
                case "SIZE":
                    if (fs.TryGet(arg, out var se)) Reply(213, se.Size.ToString(CultureInfo.InvariantCulture)); else Reply(550, "No such file");
                    return true;
                case "MDTM":
                    if (fs.TryGet(arg, out var de)) Reply(213, Ts(de.Modified)); else Reply(550, "No such file");
                    return true;
                case "REST":
                    if (long.TryParse(arg, out _rest) && _rest >= 0) Reply(350, $"Restarting at {_rest}. Send RETR or STOR"); else { _rest = 0; Reply(501, "Bad offset"); }
                    return true;
                case "RETR": return Retr(arg);
                case "STOR": case "APPE": return Stor(arg, cmd == "APPE");
                case "STOU": return Stor("upload-" + Guid.NewGuid().ToString("N")[..8], false);
                case "DELE":
                    if (fs.Remove(arg)) Reply(250, "Deleted"); else if (fs.TryGet(arg, out _)) Reply(550, "Pattern files are read-only"); else Reply(550, "No such file");
                    return true;
                case "RNFR":
                    if (fs.TryGet(arg, out var rf) && rf.IsSink) { _renameFrom = rf.Name; Reply(350, "Ready for RNTO"); } else Reply(550, "Cannot rename that");
                    return true;
                case "RNTO":
                    if (_renameFrom != null && fs.TryGet(_renameFrom, out var re) && VirtualFs.IsValidNewName(VirtualFs.Normalize(arg)) && fs.Rename(re, arg, false)) Reply(250, "Renamed"); else Reply(553, "Rename failed");
                    _renameFrom = null; return true;
                case "MKD": case "XMKD": case "RMD": case "XRMD": Reply(550, "Directories are not supported"); return true;
                case "SITE": Reply(200, "OK"); return true;
                case "STAT":
                    if (arg.Length == 0) ReplyMulti(211, "memserve status", [$"Connected from {Wire.SafeRemote(ctl)}", $"TYPE {(_binary ? "I" : "A")}", $"Pattern {Sizes.Human(fs.Backing.Size)} in RAM"], "End");
                    else ReplyMulti(213, "Status of " + arg, fs.List().Where(e => IsRoot(arg) || e.Name.Equals(VirtualFs.Normalize(arg), StringComparison.OrdinalIgnoreCase)).Select(LsLine), "End");
                    return true;
                case "ABOR": Reply(226, "No transfer to abort"); return true;
                case "AUTH": return Auth(arg);
                case "PBSZ":
                    if (_c.IsTls) Reply(200, "PBSZ=0"); else Reply(503, "Use AUTH TLS first");
                    return true;
                case "PROT":
                    if (!_c.IsTls) { Reply(503, "Use AUTH TLS first"); return true; }
                    if (arg.Equals("P", StringComparison.OrdinalIgnoreCase)) { _protP = true; Reply(200, "Data connections will be encrypted"); }
                    else if (arg.Equals("C", StringComparison.OrdinalIgnoreCase)) { _protP = false; Reply(200, "Data connections in clear"); }
                    else Reply(504, "Only PROT P and PROT C are supported");
                    return true;
                case "CLNT": Reply(200, "Noted"); return true;
                default: Reply(502, $"Command {cmd} not implemented"); return true;
            }
        }

        private bool Auth(string arg)
        {
            if (srv.Tls == null) { Reply(502, "TLS is not enabled on this server"); return true; }
            if (_c.IsTls) { Reply(503, "Already using TLS"); return true; }
            if (!arg.ToUpperInvariant().StartsWith("TLS") && !arg.Equals("SSL", StringComparison.OrdinalIgnoreCase)) { Reply(504, "Use AUTH TLS"); return true; }
            Reply(234, "Proceed with TLS negotiation");
            _c = srv.Tls.Accept(ctl, http: false); // a failed handshake ends the session
            _rxLen = 0;
            return true;
        }

        /// <summary>Plain or (after PROT P) TLS view of an accepted data connection; null if the TLS handshake fails.</summary>
        private Conn? WrapData(Socket d)
        {
            if (!_protP) return Conn.Plain(d);
            try { return srv.Tls!.Accept(d, http: false); }
            catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException) { Log.Debug($"ftp data TLS failed: {e.Message}"); return null; }
        }

        private static bool IsRoot(string p) => p is "" or "/" or "." or "./" or "\\" || p.Trim('/', '\\') == "";
        private static string StripOptions(string arg)
        {
            var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith('-'));
            return string.Join(' ', parts);
        }
        private static string Ts(DateTime t) => t.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        private static string LsLine(VEntry e)
        {
            var t = e.Modified;
            string date = (DateTime.UtcNow - t).TotalDays > 180 ? t.ToString("MMM dd  yyyy", CultureInfo.InvariantCulture) : t.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
            return $"{(e.IsSink ? "-rw-rw-rw-" : "-r--r--r--")}   1 memserve memserve {e.Size,15} {date} {e.Name}";
        }
        private static string MlsxFact(VEntry e, bool withName) => $"type=file;size={e.Size};modify={Ts(e.Modified)};perm={(e.IsSink ? "adfrw" : "r")}; {e.Name}";

        // ---------------- data connection management

        private IPAddress LocalIp()
        {
            var ip = ((IPEndPoint)ctl.LocalEndPoint!).Address;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            return ip;
        }
        private static IPAddress RemoteIp(Socket s)
        {
            var ip = ((IPEndPoint)s.RemoteEndPoint!).Address;
            return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        }

        private bool Pasv(bool extended)
        {
            _pasvListener?.Close(); _pasvListener = null; _activeTarget = null;
            var local = LocalIp();
            Socket? l = null;
            for (int attempt = 0; attempt < (opt.FtpPasvMax - opt.FtpPasvMin + 1); attempt++)
            {
                int port = srv.NextPasvPort();
                try
                {
                    l = new Socket(local.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    l.Bind(new IPEndPoint(local, port));
                    l.Listen(1);
                    break;
                }
                catch (SocketException) { l?.Dispose(); l = null; }
            }
            if (l == null) { Reply(425, "No free passive port"); return true; }
            _pasvListener = l;
            int p = ((IPEndPoint)l.LocalEndPoint!).Port;
            if (extended) { Reply(229, $"Entering Extended Passive Mode (|||{p}|)"); return true; }
            var adv = opt.FtpPasvAddr ?? local;
            if (adv.AddressFamily != AddressFamily.InterNetwork)
            {
                // PASV on an IPv6 control connection cannot express the address; most clients use EPSV instead
                Reply(425, "Use EPSV for IPv6"); return true;
            }
            var b = adv.GetAddressBytes();
            Reply(227, $"Entering Passive Mode ({b[0]},{b[1]},{b[2]},{b[3]},{p / 256},{p % 256})");
            return true;
        }

        private bool Port(string arg)
        {
            var p = arg.Split(',');
            if (p.Length != 6 || !p.All(x => int.TryParse(x, out _))) { Reply(501, "Bad PORT"); return true; }
            _pasvListener?.Close(); _pasvListener = null;
            var ip = IPAddress.Parse($"{p[0]}.{p[1]}.{p[2]}.{p[3]}");
            _activeTarget = new IPEndPoint(ip, int.Parse(p[4]) * 256 + int.Parse(p[5]));
            Reply(200, "PORT ok"); return true;
        }

        private bool Eprt(string arg)
        {
            // |1|ip4|port| or |2|ip6|port|
            if (arg.Length < 7) { Reply(501, "Bad EPRT"); return true; }
            char d = arg[0];
            var p = arg.Split(d);
            if (p.Length < 5 || !IPAddress.TryParse(p[2], out var ip) || !int.TryParse(p[3], out var port)) { Reply(501, "Bad EPRT"); return true; }
            _pasvListener?.Close(); _pasvListener = null;
            _activeTarget = new IPEndPoint(ip, port);
            Reply(200, "EPRT ok"); return true;
        }

        private Socket? OpenData()
        {
            if (_pasvListener != null)
            {
                var l = _pasvListener;
                try
                {
                    if (!l.Poll(30_000_000, SelectMode.SelectRead)) return null;
                    var d = l.Accept();
                    Wire.Tune(d);
                    d.SendTimeout = 60_000; d.ReceiveTimeout = 60_000;
                    return d;
                }
                catch (SocketException) { return null; }
                finally { l.Close(); _pasvListener = null; }
            }
            if (_activeTarget != null)
            {
                try
                {
                    var d = new Socket(_activeTarget.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    d.Connect(_activeTarget);
                    Wire.Tune(d);
                    d.SendTimeout = 60_000; d.ReceiveTimeout = 60_000;
                    return d;
                }
                catch (SocketException) { return null; }
                finally { _activeTarget = null; }
            }
            return null;
        }

        private enum Listing { Ls, Names, Mlsd }

        private bool SendListing(string arg, Listing kind)
        {
            IEnumerable<VEntry> entries;
            if (IsRoot(arg)) entries = fs.List();
            else if (fs.TryGet(arg, out var one)) entries = [one];
            else { Reply(550, "No such file or directory"); return true; }

            var sb = new StringBuilder();
            foreach (var e in entries)
                sb.Append(kind switch { Listing.Ls => LsLine(e), Listing.Names => e.Name, _ => MlsxFact(e, true) }).Append("\r\n");
            Reply(150, "Here comes the directory listing");
            using var d = OpenData();
            if (d == null) { Reply(425, "Cannot open data connection"); return true; }
            using var dc = WrapData(d);
            if (dc == null) { Reply(425, "TLS negotiation on the data connection failed"); return true; }
            try { dc.SendAll(Encoding.UTF8.GetBytes(sb.ToString())); dc.ShutdownSend(); }
            catch (Exception e) when (e is SocketException or IOException) { Reply(426, "Data connection failed"); return true; }
            Reply(226, "Directory send OK");
            return true;
        }

        private bool AbortRequested()
        {
            // ABOR arrives on the control connection (possibly with Telnet IAC/IP/Synch bytes) during a transfer
            try
            {
                if (!ctl.Poll(0, SelectMode.SelectRead)) return false;
                int n = _c.Receive(_rx.AsSpan(_rxLen));
                if (n <= 0) return true;
                _rxLen += n;
                string s = Encoding.Latin1.GetString(_rx, 0, _rxLen).ToUpperInvariant();
                return s.Contains("ABOR");
            }
            catch (SocketException) { return true; }
        }

        private bool Retr(string arg)
        {
            if (!fs.TryGet(arg, out var e)) { Reply(550, "No such file"); return true; }
            long start = _rest; _rest = 0;
            if (start > e.Size) { Reply(554, "REST beyond end of file"); return true; }
            long count = e.Size - start;
            Reply(150, $"Opening BINARY data connection for {e.Name} ({count} bytes)");
            using var d = OpenData();
            if (d == null) { Reply(425, "Cannot open data connection"); return true; }
            using var dc = WrapData(d);
            if (dc == null) { Reply(425, "TLS negotiation on the data connection failed"); return true; }
            st.Transfer();
            var sw = Stopwatch.StartNew();
            long sent;
            bool aborted = false;
            try
            {
                sent = dc.SendPattern(fs.Backing, start, count, st, default, () => aborted = AbortRequested());
                dc.ShutdownSend();
            }
            catch (Exception ex) when (ex is SocketException or IOException) { Reply(426, "Connection closed; transfer aborted"); return true; }
            if (aborted)
            {
                // consume the ABOR line from the control buffer
                int idx = Array.IndexOf(_rx, (byte)'\n', 0, _rxLen);
                if (idx >= 0) { _rxLen -= idx + 1; Buffer.BlockCopy(_rx, idx + 1, _rx, 0, _rxLen); } else _rxLen = 0;
                Reply(426, "Transfer aborted");
                Reply(226, "Abort successful");
                return true;
            }
            Reply(226, $"Transfer complete: {sent} bytes in {sw.Elapsed.TotalSeconds:0.00}s = {Sizes.Gbps(sent / Math.Max(sw.Elapsed.TotalSeconds, 1e-9))}");
            return true;
        }

        private bool Stor(string arg, bool append)
        {
            string name = VirtualFs.Normalize(arg);
            if (!VirtualFs.IsValidNewName(name)) { Reply(553, "Bad file name"); return true; }
            if (fs.TryGet(name, out var existing) && !existing.IsSink && !existing.Transient) { Reply(553, "Pattern files are read-only; choose another name"); return true; }
            long start = append && existing is { IsSink: true } ? existing.Size : _rest; _rest = 0;
            Reply(150, "Ok to send data (it will be counted and discarded)");
            using var d = OpenData();
            if (d == null) { Reply(425, "Cannot open data connection"); return true; }
            using var dc = WrapData(d);
            if (dc == null) { Reply(425, "TLS negotiation on the data connection failed"); return true; }
            var sink = append && existing is { IsSink: true } ? existing : fs.CreateSink(name);
            st.Transfer();
            var sw = Stopwatch.StartNew();
            long got;
            try { got = dc.Discard(long.MaxValue, st); }
            catch (Exception ex) when (ex is SocketException or IOException) { Reply(426, "Connection closed; transfer aborted"); return true; }
            sink.Extend(start + got);
            Reply(226, $"Transfer complete: {got} bytes in {sw.Elapsed.TotalSeconds:0.00}s = {Sizes.Gbps(got / Math.Max(sw.Elapsed.TotalSeconds, 1e-9))}");
            return true;
        }
    }
}
