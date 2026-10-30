using System.Net.Security;
using System.Net.Sockets;
using MemServe.Native;

namespace MemBench;

/// <summary>Client side of one TLS connection: OpenSSL with kernel TLS (default on Linux) or .NET SslStream.</summary>
internal abstract class ClientTls : IDisposable
{
    public abstract int Read(byte[] buf, int off, int len);
    public abstract void Write(ReadOnlySpan<byte> data);
    /// <summary>The kernel encrypts our sends: plain sendfile() on the socket produces TLS records.</summary>
    public virtual bool KernelTx => false;
    public abstract string Describe();
    public abstract void Dispose();

    /// <summary>Handshake with the configured engine. --tls-rx discard keeps SslStream: its MSG_TRUNC trick needs a socket the kernel does not decrypt.</summary>
    public static ClientTls Connect(Socket s, BenchOptions o, string? alpn)
    {
        if (o.Ktls != "off" && !o.TlsRxDiscard && Platform.Linux)
        {
            try { return OsslTls.Handshake(s, o, alpn); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        }
        return new SslStreamTls(Net.TlsClient(s, o, alpn));
    }
}

internal sealed class SslStreamTls(SslStream ssl) : ClientTls
{
    public override int Read(byte[] buf, int off, int len) => ssl.Read(buf, off, len);
    public override void Write(ReadOnlySpan<byte> data) => ssl.Write(data);
    public override string Describe() => $"{Net.Describe(ssl)}, client SslStream (user space)";
    public override void Dispose() => ssl.Dispose();
}

/// <summary>
/// OpenSSL handshake, then kernel TLS: reads use multi-record recvmsg() (the kernel decrypts into our buffer),
/// sends of the pattern use sendfile() (the kernel encrypts). Falls back to SSL_read/SSL_write per direction.
/// </summary>
internal sealed unsafe class OsslTls : ClientTls
{
    private static readonly Lock CtxLock = new();
    private static IntPtr s_ctxHttp, s_ctxOther;

    private IntPtr _ssl;
    private readonly Socket _s;
    private readonly bool _tx, _rx;

    private OsslTls(Socket s, IntPtr ssl) { _s = s; _ssl = ssl; _tx = OpenSsl.KtlsSend(ssl); _rx = OpenSsl.KtlsRecv(ssl); }

    public override bool KernelTx => _tx;

    public static OsslTls Handshake(Socket s, BenchOptions o, string? alpn)
    {
        var ctx = Context(o, alpn != null);
        OpenSsl.ERR_clear_error();
        var ssl = OpenSsl.SSL_new(ctx);
        if (ssl == IntPtr.Zero) throw new IOException(OpenSsl.LastError("SSL_new failed"));
        OpenSsl.SSL_set_fd(ssl, (int)s.Handle);
        bool isIp = System.Net.IPAddress.TryParse(o.Host, out _);
        if (!isIp)
        {
            var host = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(o.Host);
            try { OpenSsl.SSL_ctrl(ssl, OpenSsl.SSL_CTRL_SET_TLSEXT_HOSTNAME, OpenSsl.TLSEXT_NAMETYPE_host_name, host); } // SNI
            finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(host); }
        }
        if (!o.Insecure) OpenSsl.SSL_set1_host(ssl, o.Host); // name (or IP) the certificate must match
        int r = OpenSsl.SSL_connect(ssl);
        if (r != 1)
        {
            nint v = OpenSsl.SSL_get_verify_result(ssl);
            string why = v != 0 ? $"certificate: {OpenSsl.Str(OpenSsl.X509_verify_cert_error_string(v))}{(o.Insecure ? "" : " (self-signed server certificate? add -k)")}"
                                : OpenSsl.LastError($"ssl error {OpenSsl.SSL_get_error(ssl, r)}");
            OpenSsl.SSL_free(ssl);
            throw new IOException($"TLS handshake failed: {why}");
        }
        return new OsslTls(s, ssl);
    }

    private static IntPtr Context(BenchOptions o, bool http)
    {
        lock (CtxLock)
        {
            ref IntPtr slot = ref http ? ref s_ctxHttp : ref s_ctxOther;
            if (slot != IntPtr.Zero) return slot;
            var ctx = OpenSsl.SSL_CTX_new(OpenSsl.TLS_client_method());
            if (ctx == IntPtr.Zero) throw new IOException(OpenSsl.LastError("SSL_CTX_new failed"));
            OpenSsl.SSL_CTX_ctrl(ctx, OpenSsl.SSL_CTRL_SET_MIN_PROTO_VERSION, o.Cipher != null ? OpenSsl.TLS1_3_VERSION : OpenSsl.TLS1_2_VERSION, IntPtr.Zero);
            OpenSsl.SSL_CTX_set_options(ctx, OpenSsl.SSL_OP_ENABLE_KTLS);
            if (o.Cipher != null)
                OpenSsl.SSL_CTX_set_ciphersuites(ctx, o.Cipher switch { "aes128" => "TLS_AES_128_GCM_SHA256", "aes256" => "TLS_AES_256_GCM_SHA384", _ => "TLS_CHACHA20_POLY1305_SHA256" });
            if (o.Insecure) OpenSsl.SSL_CTX_set_verify(ctx, OpenSsl.SSL_VERIFY_NONE, IntPtr.Zero);
            else
            {
                OpenSsl.SSL_CTX_set_default_verify_paths(ctx);
                OpenSsl.SSL_CTX_set_verify(ctx, OpenSsl.SSL_VERIFY_PEER, IntPtr.Zero);
            }
            if (http)
            {
                byte* alpn = stackalloc byte[9];
                alpn[0] = 8;
                "http/1.1"u8.CopyTo(new Span<byte>(alpn + 1, 8));
                OpenSsl.SSL_CTX_set_alpn_protos(ctx, alpn, 9);
            }
            return slot = ctx;
        }
    }

    public override int Read(byte[] buf, int off, int len)
    {
        fixed (byte* p = &buf[off])
        {
            if (_rx && OpenSsl.SSL_has_pending(_ssl) == 0)
            {
                while (true)
                {
                    int n = Ktls.Recv((int)_s.Handle, p, len);
                    if (n >= 0) return n;
                    int err = Libc.Errno;
                    if (err == Libc.EINTR) continue;
                    if (err == Libc.EAGAIN) { _s.Poll(-1, SelectMode.SelectRead); continue; }
                    throw new IOException($"kTLS receive failed, errno {err}");
                }
            }
            OpenSsl.ERR_clear_error();
            int r = OpenSsl.SSL_read(_ssl, p, len);
            return r > 0 ? r : 0;
        }
    }

    public override void Write(ReadOnlySpan<byte> data)
    {
        fixed (byte* p0 = data)
        {
            byte* p = p0;
            int left = data.Length;
            while (left > 0)
            {
                OpenSsl.ERR_clear_error();
                int n = OpenSsl.SSL_write(_ssl, p, left);
                if (n <= 0) throw new IOException(OpenSsl.LastError($"TLS write failed (ssl error {OpenSsl.SSL_get_error(_ssl, n)})"));
                p += n; left -= n;
            }
        }
    }

    public override string Describe()
    {
        string v = OpenSsl.Str(OpenSsl.SSL_get_version(_ssl)), c = OpenSsl.Str(OpenSsl.SSL_CIPHER_get_name(OpenSsl.SSL_get_current_cipher(_ssl)));
        string k = (_tx, _rx) switch
        {
            (true, true) => "client kernel TLS (rx+tx)",
            (true, false) => "client kernel TLS tx only",
            (false, true) => "client kernel TLS rx only",
            _ => "client OpenSSL user space" + (ModuleLoaded() ? "" : " (tls module not loaded: sudo modprobe tls)"),
        };
        return $"{v} {c}, {k}";
    }

    private static bool ModuleLoaded()
    {
        try { return File.ReadAllText("/proc/sys/net/ipv4/tcp_available_ulp").Split(' ', '\n').Contains("tls"); }
        catch (IOException) { return false; }
    }

    public override void Dispose()
    {
        var ssl = Interlocked.Exchange(ref _ssl, IntPtr.Zero);
        if (ssl != IntPtr.Zero) OpenSsl.SSL_free(ssl);
    }
}
