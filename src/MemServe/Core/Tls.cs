using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MemServe.Native;

namespace MemServe.Core;

/// <summary>
/// Server TLS: a PEM certificate from disk, or a self-signed ECDSA P-256 one made at start-up.
/// Engine "openssl" (default on Linux) talks to libssl directly so the kernel can take over the crypto (kTLS);
/// engine "sslstream" is .NET's SslStream (portable fallback, or --ktls off).
/// </summary>
public sealed class Tls
{
    public readonly X509Certificate2 Certificate;
    public readonly bool SelfSigned;
    public readonly SslServerAuthenticationOptions Http, Ftp;
    private readonly IntPtr _osslHttp, _osslFtp;   // SSL_CTX*, zero when the engine is SslStream
    public readonly string Engine;
    public readonly string? EngineNote;

    private Tls(X509Certificate2 cert, bool selfSigned, string ktls)
    {
        Certificate = cert;
        SelfSigned = selfSigned;
        var ctx = SslStreamCertificateContext.Create(cert, additionalCertificates: null, offline: true);
        SslServerAuthenticationOptions Make(string? alpn) => new()
        {
            ServerCertificateContext = ctx,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            ApplicationProtocols = alpn == null ? null : [new SslApplicationProtocol(alpn)],
        };
        Http = Make("http/1.1");
        Ftp = Make(null);

        Engine = "sslstream";
        if (ktls != "off" && OperatingSystem.IsLinux())
        {
            try
            {
                _osslHttp = NewContext(cert, ktls == "zerocopy", alpn: true);
                _osslFtp = NewContext(cert, ktls == "zerocopy", alpn: false);
                Engine = "openssl";
                EngineNote = KernelTlsLoaded()
                    ? $"kTLS: kernel encrypts, sendfile stays zero-copy{(ktls == "zerocopy" ? " (TX zerocopy sendfile)" : "")}"
                    : "kernel tls module not loaded, OpenSSL encrypts in user space (run `sudo modprobe tls` for kTLS)";
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                EngineNote = $"OpenSSL unavailable ({e.Message}); using SslStream";
            }
        }
    }

    /// <summary>Server side of a TLS handshake on an accepted socket, with the configured engine.</summary>
    public Conn Accept(Socket s, bool http)
    {
        var ctx = http ? _osslHttp : _osslFtp;
        return ctx != IntPtr.Zero ? OsslConn.Accept(s, ctx) : Conn.TlsServer(s, http ? Http : Ftp);
    }

    public string Describe()
        => $"{(SelfSigned ? "self-signed" : "from file")} {Certificate.GetNameInfo(X509NameType.SimpleName, false)}, " +
           $"sha256 {Certificate.GetCertHashString(HashAlgorithmName.SHA256)[..16]}..., engine {Engine}{(EngineNote != null ? ": " + EngineNote : "")}";

    public static bool KernelTlsLoaded()
    {
        try { return File.ReadAllText("/proc/sys/net/ipv4/tcp_available_ulp").Split(' ', '\n').Contains("tls"); }
        catch (IOException) { return false; }
    }

    private static unsafe IntPtr NewContext(X509Certificate2 cert, bool zerocopy, bool alpn)
    {
        var ctx = OpenSsl.SSL_CTX_new(OpenSsl.TLS_server_method());
        if (ctx == IntPtr.Zero) throw new InvalidOperationException(OpenSsl.LastError("SSL_CTX_new failed"));
        OpenSsl.SSL_CTX_ctrl(ctx, OpenSsl.SSL_CTRL_SET_MIN_PROTO_VERSION, OpenSsl.TLS1_2_VERSION, IntPtr.Zero);
        OpenSsl.SSL_CTX_set_options(ctx, OpenSsl.SSL_OP_ENABLE_KTLS | (zerocopy ? OpenSsl.SSL_OP_ENABLE_KTLS_TX_ZEROCOPY_SENDFILE : 0));
        // TLS 1.2: only AEAD suites the kernel can take over (TLS 1.3's defaults all qualify)
        OpenSsl.SSL_CTX_set_cipher_list(ctx, "ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-ECDSA-AES256-GCM-SHA384:ECDHE-ECDSA-CHACHA20-POLY1305:" +
                                             "ECDHE-RSA-AES128-GCM-SHA256:ECDHE-RSA-AES256-GCM-SHA384:ECDHE-RSA-CHACHA20-POLY1305");
        if (alpn) OpenSsl.SSL_CTX_set_alpn_select_cb(ctx, OpenSsl.AlpnHttp11Ptr, IntPtr.Zero);

        byte[] der = cert.RawData;
        byte[] key = cert.GetECDsaPrivateKey()?.ExportPkcs8PrivateKey()
                     ?? cert.GetRSAPrivateKey()?.ExportPkcs8PrivateKey()
                     ?? throw new InvalidOperationException("certificate has no ECDSA or RSA private key");
        fixed (byte* d = der)
        fixed (byte* k = key)
        {
            byte* dp = d, kp = k;
            var x509 = OpenSsl.d2i_X509(IntPtr.Zero, ref dp, der.Length);
            var pkey = OpenSsl.d2i_AutoPrivateKey(IntPtr.Zero, ref kp, key.Length);
            try
            {
                if (x509 == IntPtr.Zero || pkey == IntPtr.Zero ||
                    OpenSsl.SSL_CTX_use_certificate(ctx, x509) != 1 || OpenSsl.SSL_CTX_use_PrivateKey(ctx, pkey) != 1 ||
                    OpenSsl.SSL_CTX_check_private_key(ctx) != 1)
                    throw new InvalidOperationException(OpenSsl.LastError("loading the certificate into OpenSSL failed"));
            }
            finally
            {
                // the context holds its own references
                if (x509 != IntPtr.Zero) OpenSsl.X509_free(x509);
                if (pkey != IntPtr.Zero) OpenSsl.EVP_PKEY_free(pkey);
            }
        }
        CryptographicOperations.ZeroMemory(key);
        return ctx;
    }

    public static Tls Create(string? certPem, string? keyPem, string ktls = "auto")
    {
        if (certPem != null)
        {
            var pem = X509Certificate2.CreateFromPemFile(certPem, keyPem ?? certPem);
            // re-import so the private key is usable by SslStream on every platform
            return new Tls(X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null), false, ktls);
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=memserve", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        try { san.AddDnsName(Dns.GetHostName()); } catch (SocketException) { }
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // serverAuth
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return new Tls(X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null), true, ktls);
    }
}
