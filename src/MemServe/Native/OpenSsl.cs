using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MemServe.Native;

/// <summary>
/// Minimal libssl/libcrypto (OpenSSL 3) bindings for the kTLS data path: OpenSSL does the handshake, installs the
/// session keys in the kernel (SSL_OP_ENABLE_KTLS), and SSL_sendfile() then encrypts in the kernel straight from the memfd.
/// </summary>
internal static unsafe partial class OpenSsl
{
    private const string Ssl = "libssl", Crypto = "libcrypto";

    static OpenSsl() => Libc.Touch(); // the assembly's DllImport resolver maps "libssl"/"libcrypto" to the .so.3 names

    public const ulong SSL_OP_ENABLE_KTLS = 1UL << 3, SSL_OP_ENABLE_KTLS_TX_ZEROCOPY_SENDFILE = 1UL << 34;
    public const int SSL_CTRL_SET_MIN_PROTO_VERSION = 123, TLS1_2_VERSION = 0x0303;
    public const int BIO_CTRL_GET_KTLS_SEND = 73, BIO_CTRL_GET_KTLS_RECV = 76;
    public const int SSL_ERROR_SSL = 1, SSL_ERROR_WANT_READ = 2, SSL_ERROR_WANT_WRITE = 3, SSL_ERROR_SYSCALL = 5, SSL_ERROR_ZERO_RETURN = 6;
    public const int SSL_TLSEXT_ERR_OK = 0, SSL_TLSEXT_ERR_NOACK = 3;
    public const int SSL_CTRL_SET_MAX_PROTO_VERSION = 124, TLS1_3_VERSION = 0x0304;
    public const int SSL_CTRL_SET_TLSEXT_HOSTNAME = 55, TLSEXT_NAMETYPE_host_name = 0;
    public const int SSL_VERIFY_NONE = 0, SSL_VERIFY_PEER = 1;

    [LibraryImport(Ssl)] public static partial IntPtr TLS_server_method();
    [LibraryImport(Ssl)] public static partial IntPtr SSL_CTX_new(IntPtr method);
    [LibraryImport(Ssl)] public static partial void SSL_CTX_free(IntPtr ctx);
    [LibraryImport(Ssl)] public static partial ulong SSL_CTX_set_options(IntPtr ctx, ulong op);
    [LibraryImport(Ssl)] public static partial nint SSL_CTX_ctrl(IntPtr ctx, int cmd, nint larg, IntPtr parg);
    [LibraryImport(Ssl)] public static partial int SSL_CTX_use_certificate(IntPtr ctx, IntPtr x509);
    [LibraryImport(Ssl)] public static partial int SSL_CTX_use_PrivateKey(IntPtr ctx, IntPtr pkey);
    [LibraryImport(Ssl)] public static partial int SSL_CTX_check_private_key(IntPtr ctx);
    [LibraryImport(Ssl, StringMarshalling = StringMarshalling.Utf8)] public static partial int SSL_CTX_set_cipher_list(IntPtr ctx, string list);
    [LibraryImport(Ssl)] public static partial void SSL_CTX_set_alpn_select_cb(IntPtr ctx, IntPtr cb, IntPtr arg);

    // client side (membench)
    [LibraryImport(Ssl)] public static partial IntPtr TLS_client_method();
    [LibraryImport(Ssl)] public static partial void SSL_CTX_set_verify(IntPtr ctx, int mode, IntPtr cb);
    [LibraryImport(Ssl)] public static partial int SSL_CTX_set_default_verify_paths(IntPtr ctx);
    [LibraryImport(Ssl, StringMarshalling = StringMarshalling.Utf8)] public static partial int SSL_CTX_set_ciphersuites(IntPtr ctx, string suites);
    [LibraryImport(Ssl)] public static partial int SSL_CTX_set_alpn_protos(IntPtr ctx, byte* protos, uint len);
    [LibraryImport(Ssl)] public static partial int SSL_connect(IntPtr ssl);
    [LibraryImport(Ssl, StringMarshalling = StringMarshalling.Utf8)] public static partial int SSL_set1_host(IntPtr ssl, string host);
    [LibraryImport(Ssl)] public static partial nint SSL_ctrl(IntPtr ssl, int cmd, nint larg, IntPtr parg);
    [LibraryImport(Ssl)] public static partial nint SSL_get_verify_result(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial int SSL_has_pending(IntPtr ssl);
    [LibraryImport(Crypto)] public static partial IntPtr X509_verify_cert_error_string(nint n);

    [LibraryImport(Ssl)] public static partial IntPtr SSL_new(IntPtr ctx);
    [LibraryImport(Ssl)] public static partial void SSL_free(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial int SSL_set_fd(IntPtr ssl, int fd);
    [LibraryImport(Ssl)] public static partial int SSL_accept(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial int SSL_get_error(IntPtr ssl, int ret);
    [LibraryImport(Ssl)] public static partial int SSL_read(IntPtr ssl, byte* buf, int num);
    [LibraryImport(Ssl)] public static partial int SSL_write(IntPtr ssl, byte* buf, int num);
    [LibraryImport(Ssl, SetLastError = true)] public static partial nint SSL_sendfile(IntPtr ssl, int fd, long offset, nuint size, int flags);
    [LibraryImport(Ssl)] public static partial int SSL_shutdown(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial IntPtr SSL_get_wbio(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial IntPtr SSL_get_rbio(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial IntPtr SSL_get_version(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial IntPtr SSL_get_current_cipher(IntPtr ssl);
    [LibraryImport(Ssl)] public static partial IntPtr SSL_CIPHER_get_name(IntPtr cipher);

    [LibraryImport(Crypto)] public static partial IntPtr d2i_X509(IntPtr a, ref byte* pp, nint length);
    [LibraryImport(Crypto)] public static partial IntPtr d2i_AutoPrivateKey(IntPtr a, ref byte* pp, nint length);
    [LibraryImport(Crypto)] public static partial void X509_free(IntPtr x);
    [LibraryImport(Crypto)] public static partial void EVP_PKEY_free(IntPtr k);
    [LibraryImport(Crypto)] public static partial nint BIO_ctrl(IntPtr bio, int cmd, nint larg, IntPtr parg);
    [LibraryImport(Crypto)] public static partial ulong ERR_get_error();
    [LibraryImport(Crypto)] public static partial void ERR_clear_error();
    [LibraryImport(Crypto)] public static partial void ERR_error_string_n(ulong e, byte* buf, nuint len);

    public static bool KtlsSend(IntPtr ssl) => BIO_ctrl(SSL_get_wbio(ssl), BIO_CTRL_GET_KTLS_SEND, 0, IntPtr.Zero) > 0;
    public static bool KtlsRecv(IntPtr ssl) => BIO_ctrl(SSL_get_rbio(ssl), BIO_CTRL_GET_KTLS_RECV, 0, IntPtr.Zero) > 0;
    public static string Str(IntPtr p) => p == IntPtr.Zero ? "?" : Marshal.PtrToStringUTF8(p) ?? "?";

    /// <summary>The oldest queued OpenSSL error as text (and clears the queue), or a fallback.</summary>
    public static string LastError(string fallback)
    {
        ulong e = ERR_get_error();
        if (e == 0) return fallback;
        byte* buf = stackalloc byte[256];
        ERR_error_string_n(e, buf, 256);
        ERR_clear_error();
        return Marshal.PtrToStringUTF8((IntPtr)buf) ?? fallback;
    }

    /// <summary>ALPN: pick http/1.1 if the client offers it, otherwise no ALPN (clients then assume HTTP/1.1 anyway).</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int AlpnHttp11(IntPtr ssl, byte** outp, byte* outlen, byte* inp, uint inlen, IntPtr arg)
    {
        for (uint i = 0; i < inlen; i += 1u + inp[i])
        {
            byte len = inp[i];
            if (len == 8 && i + 9 <= inlen && new ReadOnlySpan<byte>(inp + i + 1, 8).SequenceEqual("http/1.1"u8))
            {
                *outp = inp + i + 1;
                *outlen = 8;
                return SSL_TLSEXT_ERR_OK;
            }
        }
        return SSL_TLSEXT_ERR_NOACK;
    }

    public static IntPtr AlpnHttp11Ptr => (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte**, byte*, byte*, uint, IntPtr, int>)&AlpnHttp11;
}
