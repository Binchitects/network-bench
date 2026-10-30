using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using MemServe.Core;
using MemServe.Native;

namespace MemBench;

/// <summary>Socket data-path helpers for the client: discard-on-receive, sendfile-on-send, TCP_INFO.</summary>
internal static unsafe class Net
{
    private const long MaxChunk = 64L << 20;
    private static bool Linux => Platform.Linux;
    [ThreadStatic] private static byte[]? t_buf;

    public static Socket Connect(string host, int port, IPAddress? bind)
    {
        var addrs = IPAddress.TryParse(host, out var ip) ? [ip] : Dns.GetHostAddresses(host);
        if (bind != null) addrs = addrs.Where(a => a.AddressFamily == bind.AddressFamily).ToArray();
        Exception? last = null;
        foreach (var a in addrs)
        {
            var s = new Socket(a.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                if (bind != null) s.Bind(new IPEndPoint(bind, 0));
                s.Connect(a, port);
                return s;
            }
            catch (SocketException e) { last = e; s.Dispose(); }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>
    /// Ends a connection now so the server stops sending at once.
    /// Plain TCP: linger 0 + connect(AF_UNSPEC) send an RST immediately.
    /// Kernel TLS: neither works (the TLS layer turns disconnect into a no-op, so a linger-0 close sends nothing at all
    /// and leaves an orphan that keeps acknowledging data). Instead shutdown() moves the socket to FIN_WAIT with
    /// receiving shut down, and the kernel answers the server's next data with an RST.
    /// shutdown() also wakes any thread blocked on the socket, in both cases.
    /// </summary>
    public static void Abort(Socket? s, bool kernelTls)
    {
        if (s == null) return;
        if (!kernelTls)
            try { s.LingerState = new LingerOption(true, 0); } catch (Exception e) when (e is ObjectDisposedException or SocketException) { }
        if (Linux && !kernelTls)
        {
            try
            {
                byte* unspec = stackalloc byte[16]; // sockaddr with sa_family = AF_UNSPEC (0)
                new Span<byte>(unspec, 16).Clear();
                Libc.connect((int)s.Handle, unspec, 16);
            }
            catch (ObjectDisposedException) { }
        }
        try { s.Shutdown(SocketShutdown.Both); } catch (Exception e) when (e is ObjectDisposedException or SocketException) { }
    }

    /// <summary>Client side of a TLS handshake on a connected socket.</summary>
    public static SslStream TlsClient(Socket s, BenchOptions o, string? alpn)
    {
        var ssl = new SslStream(new NetworkStream(s, ownsSocket: false), leaveInnerStreamOpen: false);
        var opts = new SslClientAuthenticationOptions
        {
            TargetHost = o.Host,
            EnabledSslProtocols = o.Cipher != null ? SslProtocols.Tls13 : SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
            ApplicationProtocols = alpn == null ? null : [new SslApplicationProtocol(alpn)],
            RemoteCertificateValidationCallback = o.Insecure ? (_, _, _, _) => true : null,
        };
        if (o.Cipher != null && !OperatingSystem.IsWindows())
            opts.CipherSuitesPolicy = new CipherSuitesPolicy([o.Cipher switch
            {
                "aes128" => TlsCipherSuite.TLS_AES_128_GCM_SHA256,
                "aes256" => TlsCipherSuite.TLS_AES_256_GCM_SHA384,
                _ => TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
            }]);
        try { ssl.AuthenticateAsClient(opts); }
        catch (AuthenticationException e)
        {
            ssl.Dispose();
            throw new IOException($"TLS handshake failed: {e.Message}{(o.Insecure ? "" : " (self-signed server certificate? add -k)")}", e);
        }
        return ssl;
    }

    public static string Describe(SslStream ssl) => $"{ssl.SslProtocol} {ssl.NegotiatedCipherSuite}";

    /// <summary>Receives and throws away up to max bytes in one call. Returns 0 on EOF.</summary>
    public static long Discard(Socket s, long max, bool trunc, int copyBuffer)
    {
        if (trunc && Linux)
        {
            byte dummy;
            int fd = (int)s.Handle;
            while (true)
            {
                // MSG_TRUNC on TCP: the kernel acknowledges and frees the data without copying it to us
                nint n = Libc.recv(fd, &dummy, (nuint)Math.Min(max, MaxChunk), Libc.MSG_TRUNC);
                if (n >= 0) return n;
                int err = Libc.Errno;
                if (err == Libc.EINTR) continue;
                if (err == Libc.EAGAIN) { s.Poll(-1, SelectMode.SelectRead); continue; }
                throw new SocketException(err);
            }
        }
        var buf = t_buf is { } b && b.Length >= copyBuffer ? b : t_buf = GC.AllocateUninitializedArray<byte>(copyBuffer, pinned: true);
        return s.Receive(buf, 0, (int)Math.Min(max, buf.Length), SocketFlags.None);
    }

    /// <summary>Sends up to max bytes of the pattern starting at a virtual offset, in one call. Returns bytes sent.</summary>
    public static long SendPattern(Socket s, Backing b, long virtualOffset, long max, bool sendfile)
    {
        long off = virtualOffset % b.Size;
        long want = Math.Min(Math.Min(max, b.Size - off), MaxChunk);
        int fd = (int)s.Handle;
        while (true)
        {
            nint n;
            if (sendfile && b.ZeroCopy && Linux)
            {
                long fileOff = off;
                n = Libc.sendfile(fd, b.Fd, ref fileOff, (nuint)want);
            }
            else if (Linux)
                n = Libc.send(fd, b.Ptr + off, (nuint)Math.Min(want, 1 << 20), Libc.MSG_NOSIGNAL);
            else
                return s.Send(new ReadOnlySpan<byte>(b.Ptr + off, (int)Math.Min(want, 1 << 20)), SocketFlags.None);
            if (n > 0) return n;
            if (n == 0) throw new SocketException((int)SocketError.ConnectionReset);
            int err = Libc.Errno;
            if (err == Libc.EINTR) continue;
            if (err == Libc.EAGAIN) { s.Poll(-1, SelectMode.SelectWrite); continue; }
            throw new SocketException(err);
        }
    }

    public static void SendAll(Socket s, ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            int n = s.Send(data, SocketFlags.None);
            if (n <= 0) throw new SocketException((int)SocketError.ConnectionReset);
            data = data[n..];
        }
    }

    public readonly record struct TcpStats(double RttMs, double RcvRttMs, long Retrans, long SndCwnd, long Mss);

    public static TcpStats? TcpInfo(Socket? s)
    {
        if (s == null) return null;
        if (OperatingSystem.IsWindows()) return TcpInfoWindows(s);
        if (!Linux) return null;
        try
        {
            byte* buf = stackalloc byte[232];
            int len = 232;
            if (Libc.getsockopt((int)s.Handle, Libc.IPPROTO_TCP, Libc.TCP_INFO, buf, &len) != 0 || len < 104) return null;
            uint U(int at) => *(uint*)(buf + at);
            return new TcpStats(U(68) / 1000.0, U(92) / 1000.0, U(100), U(80), U(16));
        }
        catch (ObjectDisposedException) { return null; }
    }

    /// <summary>SIO_TCP_INFO (Windows 10 1703+), TCP_INFO_v0: RttUs at 20, Mss at 4, Cwnd at 32, BytesRetrans at 68.</summary>
    private static TcpStats? TcpInfoWindows(Socket s)
    {
        try
        {
            var output = new byte[88];
            s.IOControl(unchecked((int)0xD8000027), BitConverter.GetBytes(0), output);
            uint U(int at) => BitConverter.ToUInt32(output, at);
            uint mss = Math.Max(U(4), 1);
            return new TcpStats(U(20) / 1000.0, 0, U(68) / mss, U(32) / mss, mss);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or NotSupportedException or PlatformNotSupportedException) { return null; }
    }

    /// <summary>Name and speed (bits/s, -1 unknown) of the local interface a connected socket uses.</summary>
    public static (string Name, long Bps, bool Loopback)? LinkOf(Socket? s)
    {
        if (s?.LocalEndPoint is not IPEndPoint local || s.RemoteEndPoint is not IPEndPoint remote) return null;
        var la = local.Address.IsIPv4MappedToIPv6 ? local.Address.MapToIPv4() : local.Address;
        var ra = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        if (IPAddress.IsLoopback(la) || la.Equals(ra)) return ("lo", -1, true); // same host: kernel loopback, no NIC involved
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.Equals(la))
                    {
                        long bps = -1;
                        // veth, bridges, tun and VM NICs report made-up speeds: only trust interfaces backed by a device
                        bool physical = !OperatingSystem.IsLinux() || Directory.Exists($"/sys/class/net/{ni.Name}/device");
                        if (physical) try { bps = ni.Speed; } catch { }
                        return (physical ? ni.Name : ni.Name + " (virtual)", bps > 0 ? bps : -1, ni.NetworkInterfaceType == NetworkInterfaceType.Loopback);
                    }
        }
        catch (NetworkInformationException) { }
        return null;
    }

    /// <summary>Third value of a tcp_rmem / tcp_wmem sysctl (max auto-tuned buffer), or -1.</summary>
    public static long TcpBufMax(bool receive)
    {
        try
        {
            var v = File.ReadAllText(receive ? "/proc/sys/net/ipv4/tcp_rmem" : "/proc/sys/net/ipv4/tcp_wmem").Split((char[])[' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
            return v.Length >= 3 && long.TryParse(v[2], out var m) ? m : -1;
        }
        catch (IOException) { return -1; }
        catch (UnauthorizedAccessException) { return -1; }
    }
}
