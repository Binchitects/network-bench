using System.Net.Sockets;
using System.Runtime.InteropServices;
using MemServe.Native;
using MemServe.Util;

namespace MemServe.Core;

/// <summary>Socket data-path helpers. On Linux: sendfile() from the memfd out, recv(MSG_TRUNC) in; elsewhere plain copies.</summary>
public static unsafe class Wire
{
    public const long SendChunk = 16L << 20;
    private static readonly bool Linux = OperatingSystem.IsLinux();

    private static bool _truncOk = true;
    [ThreadStatic] private static bool t_bulk;

    /// <summary>
    /// Lowers the calling thread's priority the first time it moves bulk data. With hundreds of busy connections
    /// the CPU saturates (loopback tests); this keeps accepting new connections and answering /stats fast
    /// instead of making them queue behind the data pumps for seconds. Throughput is unchanged.
    /// </summary>
    internal static void MarkBulk()
    {
        if (t_bulk) return;
        t_bulk = true;
        if (Linux) Libc.setpriority(Libc.PRIO_PROCESS, 0, Libc.BulkNice);
    }
    [ThreadStatic] private static byte[]? t_scratch;

    public static byte[] Scratch(int size) => t_scratch is { } s && s.Length >= size ? s : t_scratch = GC.AllocateUninitializedArray<byte>(Math.Max(size, 1 << 20), pinned: true);

    public static void Tune(Socket s)
    {
        try { s.NoDelay = true; } catch { }
        try { s.LingerState = new LingerOption(false, 0); } catch { }
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

    public static int Receive(Socket s, Span<byte> buf)
    {
        try { return s.Receive(buf, SocketFlags.None); }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.Shutdown or SocketError.ConnectionAborted) { return 0; }
    }

    public static bool ReceiveExact(Socket s, Span<byte> buf)
    {
        while (buf.Length > 0)
        {
            int n = Receive(s, buf);
            if (n <= 0) return false;
            buf = buf[n..];
        }
        return true;
    }

    /// <summary>
    /// Sends [header][pattern bytes virtualOffset..+count]. Header and pattern go out as one TCP stream.
    /// Returns bytes of pattern sent. abort() is polled between chunks (FTP ABOR).
    /// </summary>
    public static long SendPattern(Socket s, Backing b, long virtualOffset, long count, ProtoStats stats,
                                   ReadOnlySpan<byte> header = default, Func<bool>? abort = null)
    {
        int fd = (int)s.Handle;
        long sent = 0;
        if (count > 0) MarkBulk();
        if (Linux && b.ZeroCopy)
        {
            if (header.Length > 0)
            {
                fixed (byte* hp = header)
                {
                    byte* p = hp; nuint left = (nuint)header.Length;
                    while (left > 0)
                    {
                        nint n = Libc.send(fd, p, left, Libc.MSG_NOSIGNAL | (count > 0 ? Libc.MSG_MORE : 0));
                        if (n < 0)
                        {
                            int err = Libc.Errno;
                            if (err == Libc.EINTR) continue;
                            if (err == Libc.EAGAIN) { s.Poll(-1, SelectMode.SelectWrite); continue; }
                            throw new SocketException(err);
                        }
                        p += n; left -= (nuint)n;
                    }
                }
            }
            while (sent < count)
            {
                long off = (virtualOffset + sent) % b.Size;
                long want = Math.Min(Math.Min(count - sent, b.Size - off), SendChunk);
                long fileOff = off;
                nint n = Libc.sendfile(fd, b.Fd, ref fileOff, (nuint)want);
                if (n < 0)
                {
                    int err = Libc.Errno;
                    if (err == Libc.EINTR) continue;
                    if (err == Libc.EAGAIN) { s.Poll(-1, SelectMode.SelectWrite); continue; }
                    throw new SocketException(err);
                }
                if (n == 0) throw new SocketException((int)SocketError.ConnectionReset);
                sent += n;
                stats.AddOut(n);
                if (abort != null && abort()) break;
            }
            return sent;
        }

        // portable path: plain send from the pattern memory (one copy in the kernel, no managed copies)
        if (header.Length > 0) SendAll(s, header);
        while (sent < count)
        {
            byte* p = b.Slice(virtualOffset + sent, Math.Min(count - sent, 1 << 20), out long len);
            int n = s.Send(new ReadOnlySpan<byte>(p, (int)len), SocketFlags.None);
            if (n <= 0) throw new SocketException((int)SocketError.ConnectionReset);
            sent += n;
            stats.AddOut(n);
            if (abort != null && abort()) break;
        }
        return sent;
    }

    /// <summary>
    /// Reads and throws away up to <paramref name="count"/> bytes (long.MaxValue = until the peer closes).
    /// Linux: recv(MSG_TRUNC) - TCP acknowledges and frees the data without copying it to user space,
    /// one syscall per call and no pipe (splice pipes are capped per user, which bites in containers). Returns bytes discarded.
    /// </summary>
    public static long Discard(Socket s, long count, ProtoStats stats)
    {
        long done = 0;
        if (count > 0) MarkBulk();
        if (Linux && _truncOk)
        {
            int fd = (int)s.Handle;
            byte dummy;
            while (done < count)
            {
                nint n = Libc.recv(fd, &dummy, (nuint)Math.Min(count - done, SendChunk), Libc.MSG_TRUNC);
                if (n < 0)
                {
                    int err = Libc.Errno;
                    if (err == Libc.EINTR) continue;
                    if (err == Libc.EAGAIN) { s.Poll(-1, SelectMode.SelectRead); continue; }
                    if (err is Libc.EINVAL or Libc.EOPNOTSUPP) { _truncOk = false; break; } // fall back to recv below
                    if (err == Libc.ECONNRESET) return done;
                    throw new SocketException(err);
                }
                if (n == 0) return done; // EOF
                done += n;
                stats.AddIn(n);
            }
            if (done >= count) return done;
        }
        var buf = Scratch(1 << 20);
        while (done < count)
        {
            int n = Receive(s, buf.AsSpan(0, (int)Math.Min(buf.Length, count - done)));
            if (n <= 0) break;
            done += n;
            stats.AddIn(n);
        }
        return done;
    }

    public static Socket Listen(System.Net.IPAddress bind, int port, int backlog = 512)
    {
        Socket l;
        if (bind.AddressFamily == AddressFamily.InterNetworkV6)
        {
            l = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            try { l.DualMode = true; } catch { }
        }
        else l = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        l.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        l.Bind(new System.Net.IPEndPoint(bind, port));
        l.Listen(backlog);
        return l;
    }

    public static void AcceptLoop(Socket listener, string name, Action<Socket> handler)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                Socket c;
                try { c = listener.Accept(); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException e) { Log.Warn($"{name} accept: {e.Message}"); Thread.Sleep(50); continue; }
                Tune(c);
                var ct = new Thread(() =>
                {
                    try { handler(c); }
                    catch (SocketException) { }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                    catch (System.Security.Authentication.AuthenticationException e) { Log.Debug($"{name} TLS handshake with {SafeRemote(c)} failed: {e.Message}"); }
                    catch (Exception e) { Log.Error($"{name} connection {SafeRemote(c)}: {e}"); }
                    finally { try { c.Close(); } catch { } }
                }, 512 * 1024) { IsBackground = true, Name = name + "-conn" };
                ct.Start();
            }
        }) { IsBackground = true, Name = name + "-accept" };
        t.Start();
    }

    public static string SafeRemote(Socket s)
    {
        try { return s.RemoteEndPoint?.ToString() ?? "?"; } catch { return "?"; }
    }
}
