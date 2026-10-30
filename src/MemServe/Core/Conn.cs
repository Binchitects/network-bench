using System.Net.Security;
using System.Net.Sockets;
using MemServe.Native;

namespace MemServe.Core;

/// <summary>
/// One connection's byte stream: plain TCP (zero-copy sendfile / MSG_TRUNC) or TLS (SslStream).
/// The protocol code is written against this, so HTTP/HTTPS and FTP/FTPS share every line.
/// </summary>
public abstract class Conn(Socket socket) : IDisposable
{
    public readonly Socket Socket = socket;
    public abstract bool IsTls { get; }
    /// <summary>Reads what is available into buf; 0 on EOF.</summary>
    public abstract int Receive(Span<byte> buf);
    public abstract void SendAll(ReadOnlySpan<byte> data);
    /// <summary>Sends [header][pattern bytes virtualOffset..+count]; returns pattern bytes sent. abort() is polled between chunks.</summary>
    public abstract long SendPattern(Backing b, long virtualOffset, long count, ProtoStats stats, ReadOnlySpan<byte> header = default, Func<bool>? abort = null);
    /// <summary>Reads and throws away up to count bytes (long.MaxValue = until EOF); returns bytes discarded.</summary>
    public abstract long Discard(long count, ProtoStats stats);
    /// <summary>Ends our sending direction (TLS: close_notify first).</summary>
    public abstract void ShutdownSend();
    public virtual void Dispose() { }

    public static Conn Plain(Socket s) => new PlainConn(s);

    /// <summary>Runs the server side of a TLS handshake on an accepted socket.</summary>
    public static Conn TlsServer(Socket s, SslServerAuthenticationOptions options)
    {
        var ssl = new SslStream(new NetworkStream(s, ownsSocket: false), leaveInnerStreamOpen: false);
        try { ssl.AuthenticateAsServer(options); }
        catch { ssl.Dispose(); throw; }
        TlsCounters.Count(tx: false, rx: false); // SslStream: always user space
        return new TlsConn(s, ssl);
    }
}

internal sealed class PlainConn(Socket s) : Conn(s)
{
    public override bool IsTls => false;
    public override int Receive(Span<byte> buf) => Wire.Receive(Socket, buf);
    public override void SendAll(ReadOnlySpan<byte> data) => Wire.SendAll(Socket, data);
    public override long SendPattern(Backing b, long virtualOffset, long count, ProtoStats stats, ReadOnlySpan<byte> header = default, Func<bool>? abort = null)
        => Wire.SendPattern(Socket, b, virtualOffset, count, stats, header, abort);
    public override long Discard(long count, ProtoStats stats) => Wire.Discard(Socket, count, stats);
    public override void ShutdownSend() => Socket.Shutdown(SocketShutdown.Send);
}

internal sealed unsafe class TlsConn(Socket s, SslStream ssl) : Conn(s)
{
    /// <summary>Plaintext handed to SslStream per call; it cuts this into 16 KiB records.</summary>
    private const int Chunk = 1 << 20;

    public override bool IsTls => true;

    public override int Receive(Span<byte> buf)
    {
        try { return ssl.Read(buf); }
        catch (IOException e) when (e.InnerException is SocketException) { return 0; }
    }

    public override void SendAll(ReadOnlySpan<byte> data) => ssl.Write(data);

    public override long SendPattern(Backing b, long virtualOffset, long count, ProtoStats stats, ReadOnlySpan<byte> header = default, Func<bool>? abort = null)
    {
        if (count > 0) Wire.MarkBulk();
        if (header.Length > 0) ssl.Write(header);
        long sent = 0;
        while (sent < count)
        {
            // encrypt straight from the pattern memory: no managed copy of the plaintext
            byte* p = b.Slice(virtualOffset + sent, Math.Min(count - sent, Chunk), out long len);
            ssl.Write(new ReadOnlySpan<byte>(p, (int)len));
            sent += len;
            stats.AddOut(len);
            if (abort != null && abort()) break;
        }
        return sent;
    }

    public override long Discard(long count, ProtoStats stats)
    {
        if (count > 0) Wire.MarkBulk();
        var buf = Wire.Scratch(Chunk);
        long done = 0;
        while (done < count)
        {
            int n = Receive(buf.AsSpan(0, (int)Math.Min(buf.Length, count - done)));
            if (n <= 0) break;
            done += n;
            stats.AddIn(n);
        }
        return done;
    }

    public override void ShutdownSend()
    {
        try { ssl.ShutdownAsync().GetAwaiter().GetResult(); } catch (IOException) { }
        Socket.Shutdown(SocketShutdown.Send);
    }

    public override void Dispose() => ssl.Dispose();
}

/// <summary>
/// TLS through OpenSSL directly. When the kernel tls module is loaded OpenSSL moves the session keys into the kernel (kTLS):
/// SSL_write/SSL_read then encrypt/decrypt in the kernel and SSL_sendfile() sends the memfd with no user-space copy and
/// no per-record syscall. Without the module the same code runs OpenSSL's user-space TLS.
/// </summary>
internal sealed unsafe class OsslConn : Conn
{
    private IntPtr _ssl;
    private readonly bool _ktlsTx, _ktlsRx;

    private OsslConn(Socket s, IntPtr ssl, bool ktlsTx, bool ktlsRx) : base(s) { _ssl = ssl; _ktlsTx = ktlsTx; _ktlsRx = ktlsRx; }

    public override bool IsTls => true;

    public static OsslConn Accept(Socket s, IntPtr ctx)
    {
        OpenSsl.ERR_clear_error();
        var ssl = OpenSsl.SSL_new(ctx);
        if (ssl == IntPtr.Zero) throw new IOException(OpenSsl.LastError("SSL_new failed"));
        OpenSsl.SSL_set_fd(ssl, (int)s.Handle);
        int r = OpenSsl.SSL_accept(ssl);
        if (r != 1)
        {
            var msg = OpenSsl.LastError($"TLS handshake failed (ssl error {OpenSsl.SSL_get_error(ssl, r)})");
            OpenSsl.SSL_free(ssl);
            throw new System.Security.Authentication.AuthenticationException(msg);
        }
        bool tx = OpenSsl.KtlsSend(ssl), rx = OpenSsl.KtlsRecv(ssl);
        TlsCounters.Count(tx, rx);
        return new OsslConn(s, ssl, tx, rx);
    }

    public override int Receive(Span<byte> buf)
    {
        fixed (byte* p = buf)
        {
            if (_ktlsRx && OpenSsl.SSL_has_pending(_ssl) == 0)
            {
                // kernel TLS: several records per syscall instead of SSL_read's one
                while (true)
                {
                    int r = Ktls.Recv((int)Socket.Handle, p, buf.Length);
                    if (r >= 0) return r;
                    if (Libc.Errno != Libc.EINTR) return 0; // reset, timeout: end of stream
                }
            }
            OpenSsl.ERR_clear_error();
            int n = OpenSsl.SSL_read(_ssl, p, buf.Length);
            return n > 0 ? n : 0; // close_notify, reset, timeout: all end the stream for us
        }
    }

    public override void SendAll(ReadOnlySpan<byte> data)
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

    public override long SendPattern(Backing b, long virtualOffset, long count, ProtoStats stats, ReadOnlySpan<byte> header = default, Func<bool>? abort = null)
    {
        if (count > 0) Wire.MarkBulk();
        if (header.Length > 0) SendAll(header);
        long sent = 0;
        if (_ktlsTx && b.ZeroCopy)
        {
            // kTLS: the kernel reads the memfd pages and encrypts them while sending
            int stalls = 0;
            while (sent < count)
            {
                long off = (virtualOffset + sent) % b.Size;
                long want = Math.Min(Math.Min(count - sent, b.Size - off), Wire.SendChunk);
                OpenSsl.ERR_clear_error();
                nint n = OpenSsl.SSL_sendfile(_ssl, b.Fd, off, (nuint)want, 0);
                // 0 leaves errno untouched, so a stale EAGAIN must not be trusted: 0 means the peer is gone
                if (n == 0) throw new IOException("peer closed the connection");
                if (n < 0)
                {
                    int err = Libc.Errno;
                    if (err == Libc.EINTR) continue;
                    // wait for buffer space, but stop if the socket has failed or never drains
                    if (err == Libc.EAGAIN && ++stalls < 100 && Socket.Poll(30_000_000, SelectMode.SelectWrite) &&
                        (int)Socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error)! == 0)
                        continue;
                    throw new IOException(OpenSsl.LastError($"SSL_sendfile failed, errno {err}"));
                }
                stalls = 0;
                sent += n;
                stats.AddOut(n);
                // with kernel TLS the server can keep sending for seconds after the client hung up; a client that has
                // closed its side (FIN: CLOSE_WAIT) or reset is not going to read the rest of a bulk download
                if (Libc.TcpState((int)Socket.Handle) is not (Libc.TCP_ESTABLISHED or 0))
                    throw new IOException("client closed the connection");
                if (abort != null && abort()) break;
            }
            return sent;
        }
        while (sent < count)
        {
            byte* p = b.Slice(virtualOffset + sent, Math.Min(count - sent, 1 << 20), out long len);
            SendAll(new ReadOnlySpan<byte>(p, (int)len));
            sent += len;
            stats.AddOut(len);
            if (abort != null && abort()) break;
        }
        return sent;
    }

    public override long Discard(long count, ProtoStats stats)
    {
        if (count > 0) Wire.MarkBulk();
        var buf = Wire.Scratch(1 << 20);
        long done = 0;
        while (done < count)
        {
            int n = Receive(buf.AsSpan(0, (int)Math.Min(buf.Length, count - done)));
            if (n <= 0) break;
            done += n;
            stats.AddIn(n);
        }
        return done;
    }

    public override void ShutdownSend()
    {
        OpenSsl.ERR_clear_error();
        OpenSsl.SSL_shutdown(_ssl); // close_notify
        Socket.Shutdown(SocketShutdown.Send);
    }

    public override void Dispose()
    {
        var ssl = Interlocked.Exchange(ref _ssl, IntPtr.Zero);
        if (ssl != IntPtr.Zero) OpenSsl.SSL_free(ssl);
    }
}

/// <summary>How TLS connections ended up being served (kernel or user-space crypto), reported in /stats.</summary>
public static class TlsCounters
{
    public static long KtlsTx, KtlsRx, Userspace;
    public static void Count(bool tx, bool rx)
    {
        if (tx) Interlocked.Increment(ref KtlsTx);
        if (rx) Interlocked.Increment(ref KtlsRx);
        if (!tx && !rx) Interlocked.Increment(ref Userspace);
    }
}
