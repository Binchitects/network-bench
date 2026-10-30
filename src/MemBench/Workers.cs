using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using MemServe.Core;
using MemServe.Native;

namespace MemBench;

/// <summary>Live state of one worker, read by the reporter thread.</summary>
public sealed class WorkerState(int index)
{
    public readonly int Index = index;
    public long Bytes;            // written only by the worker
    public long Requests;
    public volatile int Tid;
    public volatile Socket? Sock; // current data socket (closed by the main thread to stop the worker)
    public volatile bool Ready, Done;   // Ready: first bytes moved
    public volatile string? Error;
    public volatile string? Note;
    public volatile string? TlsInfo; // negotiated protocol and cipher of the first TLS connection
    public volatile bool KernelTls;  // the current socket is kernel TLS (changes how it must be aborted)
}

internal abstract class Worker(BenchOptions o, WorkerState st, Backing? backing, CancellationToken stop)
{
    protected readonly BenchOptions O = o;
    protected readonly WorkerState St = st;
    protected readonly Backing? B = backing;
    protected readonly CancellationToken Stop = stop;
    /// <summary>TLS over the current data connection, or null for plain TCP.</summary>
    protected ClientTls? Tls;

    public void ThreadMain()
    {
        St.Tid = Platform.CurrentThreadId();
        Platform.LowerThreadPriority(); // same as the server: bulk threads yield to the sampling thread
        try { Run(); }
        catch (Exception e) when (Stop.IsCancellationRequested && e is SocketException or IOException or ObjectDisposedException) { }
        catch (Exception e) { St.Error = e is SocketException se ? $"{se.SocketErrorCode}: {se.Message}" : e.Message; }
        finally { St.Done = true; }
    }

    protected abstract void Run();

    protected void Count(long n)
    {
        Volatile.Write(ref St.Bytes, St.Bytes + n);
        if (n > 0 && !St.Ready) St.Ready = true; // "ready" = data is flowing, not just connected
    }


    protected ClientTls StartTls(Socket s, string? alpn)
    {
        var tls = ClientTls.Connect(s, O, alpn);
        St.KernelTls = tls is OsslTls;
        St.TlsInfo ??= tls.Describe();
        return tls;
    }

    protected void Send(Socket s, ReadOnlySpan<byte> data)
    {
        if (Tls != null) Tls.Write(data); else Net.SendAll(s, data);
    }

    protected int Receive(Socket s, byte[] buf, int off, int len) => Tls != null ? Tls.Read(buf, off, len) : s.Receive(buf, off, len, SocketFlags.None);

    /// <summary>Discards a body of `length` bytes (long.MaxValue = until EOF). Returns false on EOF / stop.</summary>
    protected bool DrainBody(Socket s, long length)
    {
        if (Tls != null && O.TlsRxDiscard)
        {
            // drop the encrypted records unread: the client does no crypto, only the server is measured
            while (!Stop.IsCancellationRequested)
            {
                long n = Net.Discard(s, long.MaxValue, true, (int)O.Buffer);
                if (n <= 0) return false;
                Count(n);
            }
            return false;
        }
        var buf = Tls != null ? _plain ??= GC.AllocateUninitializedArray<byte>((int)Math.Max(O.Buffer, 16 * 1024)) : null;
        while (length > 0 && !Stop.IsCancellationRequested)
        {
            long n = Tls != null ? Tls.Read(buf!, 0, (int)Math.Min(buf!.Length, length)) : Net.Discard(s, length, O.RxTrunc, (int)O.Buffer);
            if (n <= 0) return length == long.MaxValue;
            Count(n);
            if (length != long.MaxValue) length -= n;
        }
        return !Stop.IsCancellationRequested;
    }

    private byte[]? _plain;

    protected unsafe void Blast(Socket s)
    {
        long off = (long)St.Index * (B!.Size / Math.Max(1, O.Connections)); // different start per worker
        while (!Stop.IsCancellationRequested)
        {
            long n;
            if (Tls is { KernelTx: true } && O.TxSendfile)
                n = Net.SendPattern(s, B, off, long.MaxValue, true); // kernel TLS: sendfile, the kernel encrypts
            else if (Tls != null)
            {
                // user-space TLS: encrypt straight from the pattern memory
                long po = off % B.Size;
                n = Math.Min(1 << 20, B.Size - po);
                Tls.Write(new ReadOnlySpan<byte>(B.Ptr + po, (int)n));
            }
            else n = Net.SendPattern(s, B, off, long.MaxValue, O.TxSendfile);
            off += n;
            Count(n);
        }
    }
}

/// <summary>HTTP/1.1: repeated keep-alive GETs drained with MSG_TRUNC, or one endless PUT fed by sendfile.</summary>
internal sealed class HttpWorker(BenchOptions o, WorkerState st, Backing? b, CancellationToken stop) : Worker(o, st, b, stop)
{
    private readonly byte[] _head = new byte[16 * 1024];

    protected override void Run()
    {
        while (!Stop.IsCancellationRequested)
        {
            using var s = Net.Connect(O.Host, O.Port, O.Bind);
            St.Sock = s;
            using var tls = Tls = O.Tls ? StartTls(s, "http/1.1") : null;
            if (O.Upload)
            {
                // a body far larger than any test: the server drains it until we close the connection
                Send(s, Encoding.ASCII.GetBytes($"PUT {O.Path} HTTP/1.1\r\nHost: {O.Host}\r\nContent-Type: application/octet-stream\r\nContent-Length: {1L << 50}\r\n\r\n"));
                Blast(s);
                return;
            }
            var req = Encoding.ASCII.GetBytes($"GET {O.Path} HTTP/1.1\r\nHost: {O.Host}\r\nUser-Agent: membench\r\nAccept: */*\r\n\r\n");
            while (!Stop.IsCancellationRequested)
            {
                Send(s, req);
                if (!ReadResponse(s)) break; // server closed (or --tls-rx discard): reconnect
                Interlocked.Increment(ref St.Requests);
            }
        }
    }

    private bool ReadResponse(Socket s)
    {
        int have = 0, end;
        while ((end = _head.AsSpan(0, have).IndexOf("\r\n\r\n"u8)) < 0)
        {
            if (have == _head.Length) throw new IOException("HTTP response header too large");
            int n = Receive(s, _head, have, _head.Length - have);
            if (n <= 0) return false;
            have += n;
        }
        var head = Encoding.Latin1.GetString(_head, 0, end);
        var lines = head.Split("\r\n");
        var status = lines[0].Split(' ', 3);
        if (status.Length < 2 || !int.TryParse(status[1], out int code)) throw new IOException($"bad HTTP status line '{lines[0]}'");
        if (code is < 200 or >= 300) throw new IOException($"server answered '{lines[0]}' for {O.Path}");
        long length = long.MaxValue;
        bool close = false;
        foreach (var l in lines.Skip(1))
        {
            int c = l.IndexOf(':');
            if (c <= 0) continue;
            var name = l[..c].Trim();
            var val = l[(c + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) length = long.Parse(val, CultureInfo.InvariantCulture);
            else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) && val.Contains("close", StringComparison.OrdinalIgnoreCase)) close = true;
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) { length = long.MaxValue; close = true; } // chunked: count raw bytes until EOF
        }
        long inHead = have - (end + 4);
        if (length != long.MaxValue) inHead = Math.Min(inHead, length);
        Count(inHead);
        if (length != long.MaxValue) length -= inHead;
        bool ok = DrainBody(s, length);
        return ok && !close && length != long.MaxValue;
    }
}

/// <summary>FTP: passive (EPSV, then PASV) data connections; RETR repeated, or one endless STOR.</summary>
internal sealed class FtpWorker(BenchOptions o, WorkerState st, Backing? b, CancellationToken stop) : Worker(o, st, b, stop)
{
    private Stream _cs = null!;   // control channel: plain, or TLS after AUTH TLS
    private readonly byte[] _rx = new byte[8192];
    private int _rxLen;

    protected override void Run()
    {
        using var ctl = Net.Connect(O.Host, O.Port, O.Bind);
        using var cs = _cs = new NetworkStream(ctl, ownsSocket: false);
        Expect(Reply(), 2, "greeting");
        Stream? ctlTls = null;
        if (O.Tls)
        {
            Expect(Cmd("AUTH TLS"), 2, "AUTH TLS (server without FTPS?)");
            _cs = ctlTls = Net.TlsClient(ctl, O, null);
            Expect(Cmd("PBSZ 0"), 2, "PBSZ");
            Expect(Cmd("PROT P"), 2, "PROT P");
        }
        using var ctlTlsGuard = ctlTls;
        var (code, text) = Cmd($"USER {O.User}");
        if (code == 331) (code, text) = Cmd($"PASS {O.Pass}");
        if (code / 100 != 2) throw new IOException($"FTP login failed: {code} {text}");
        Expect(Cmd("TYPE I"), 2, "TYPE I");
        var remote = ((IPEndPoint)ctl.RemoteEndPoint!).Address;

        while (!Stop.IsCancellationRequested)
        {
            var ep = Passive(remote);
            using var data = Net.Connect(ep.Address.ToString(), ep.Port, O.Bind);
            St.Sock = data;
            string name = O.Upload ? (O.Connections > 1 ? $"{O.Path}.{St.Index}" : O.Path) : O.Path;
            var (c, t) = Cmd((O.Upload ? "STOR " : "RETR ") + name);
            if (c / 100 != 1) throw new IOException($"{(O.Upload ? "STOR" : "RETR")} {name}: {c} {t}");
            using var tls = Tls = O.Tls ? StartTls(data, null) : null; // FTP server is the TLS server on data connections
            if (O.Upload) { Blast(data); return; }
            DrainBody(data, long.MaxValue);
            tls?.Dispose();
            Tls = null;
            data.Close();
            if (Stop.IsCancellationRequested) return;
            Expect(Reply(), 2, "transfer complete");
            Interlocked.Increment(ref St.Requests);
        }
    }

    private IPEndPoint Passive(IPAddress remote)
    {
        var (code, text) = Cmd("EPSV");
        if (code == 229)
        {
            var p = text[(text.IndexOf('(') + 1)..text.LastIndexOf(')')].Split(text[text.IndexOf('(') + 1]);
            return new IPEndPoint(remote, int.Parse(p[3], CultureInfo.InvariantCulture));
        }
        (code, text) = Cmd("PASV");
        if (code != 227) throw new IOException($"passive mode refused: {code} {text}");
        var n = text[(text.IndexOf('(') + 1)..text.LastIndexOf(')')].Split(',').Select(x => int.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
        var ip = new IPAddress([(byte)n[0], (byte)n[1], (byte)n[2], (byte)n[3]]);
        // servers behind NAT often announce an unroutable address; the control connection's peer is a safer bet
        if (ip.Equals(IPAddress.Any)) ip = remote;
        return new IPEndPoint(ip, n[4] * 256 + n[5]);
    }

    private (int, string) Cmd(string line)
    {
        _cs.Write(Encoding.UTF8.GetBytes(line + "\r\n"));
        return Reply();
    }

    private static void Expect((int Code, string Text) r, int cls, string what)
    {
        if (r.Code / 100 != cls) throw new IOException($"FTP {what}: {r.Code} {r.Text}");
    }

    private (int, string) Reply()
    {
        string first = ReadLine();
        if (first.Length < 3 || !int.TryParse(first[..3], out int code)) throw new IOException($"bad FTP reply '{first}'");
        if (first.Length > 3 && first[3] == '-')
            while (true) { var l = ReadLine(); if (l.StartsWith(first[..3] + " ")) break; }
        return (code, first.Length > 4 ? first[4..] : "");
    }

    private string ReadLine()
    {
        while (true)
        {
            int idx = Array.IndexOf(_rx, (byte)'\n', 0, _rxLen);
            if (idx >= 0)
            {
                var s = Encoding.UTF8.GetString(_rx, 0, idx).TrimEnd('\r');
                _rxLen -= idx + 1;
                Buffer.BlockCopy(_rx, idx + 1, _rx, 0, _rxLen);
                return s;
            }
            if (_rxLen == _rx.Length) throw new IOException("FTP reply line too long");
            int n = _cs.Read(_rx, _rxLen, _rx.Length - _rxLen);
            if (n <= 0) throw new IOException("FTP control connection closed");
            _rxLen += n;
        }
    }
}

/// <summary>File I/O on a path (e.g. a cifs mount of the Samba share): striped pread/pwrite, O_DIRECT by default.</summary>
internal sealed unsafe class FileWorker(BenchOptions o, WorkerState st, Backing? b, CancellationToken stop) : Worker(o, st, b, stop)
{
    protected override void Run()
    {
        if (Platform.Linux) RunLinux(); else RunPortable();
    }

    private void RunLinux()
    {
        int flags = O.Upload ? Libc.O_WRONLY | Libc.O_CREAT : Libc.O_RDONLY;
        int fd = O.Direct ? Libc.OpenMode(O.Path, flags | Libc.O_DIRECT | Libc.O_CLOEXEC, 0x1A4) : -1;
        if (fd < 0)
        {
            if (O.Direct && Libc.Errno != Libc.EINVAL) throw new IOException($"open {O.Path}: errno {Libc.Errno}");
            if (O.Direct) St.Note = "O_DIRECT not supported here: buffered I/O, repeated reads may be served by the local page cache";
            fd = Libc.OpenMode(O.Path, flags | Libc.O_CLOEXEC, 0x1A4);
            if (fd < 0) throw new IOException($"open {O.Path}: errno {Libc.Errno}");
        }
        long block = (O.Block + 4095) & ~4095L;
        byte* buf = (byte*)NativeMemory.AlignedAlloc((nuint)block, 4096);
        try
        {
            long size = O.Upload ? O.FileSize : new FileInfo(O.Path).Length;
            if (size < block) throw new IOException($"{O.Path} is smaller than one block ({block} bytes); use --block");
            if (O.Upload) Buffer.MemoryCopy(B!.Ptr, buf, block, Math.Min(block, B.Size));
            long stride = block * O.Connections, start = block * St.Index;
            long off = start;
            while (!Stop.IsCancellationRequested)
            {
                if (off + block > size) off = start;
                nint n = O.Upload ? Libc.pwrite(fd, buf, (nuint)block, off) : Libc.pread(fd, buf, (nuint)block, off);
                if (n < 0)
                {
                    if (Libc.Errno == Libc.EINTR) continue;
                    throw new IOException($"{(O.Upload ? "pwrite" : "pread")} at {off}: errno {Libc.Errno}");
                }
                if (n == 0) { off = start; continue; }
                Count(n);
                Interlocked.Increment(ref St.Requests);
                off += stride;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
            Libc.close(fd);
        }
    }

    /// <summary>
    /// Windows / other OS: RandomAccess on a handle. On Windows FILE_FLAG_NO_BUFFERING (+ write-through for writes)
    /// keeps the client cache out of the way, like O_DIRECT; it needs sector-aligned buffers, offsets and sizes.
    /// </summary>
    private void RunPortable()
    {
        const FileOptions NoBuffering = (FileOptions)0x20000000; // FILE_FLAG_NO_BUFFERING
        bool direct = O.Direct && OperatingSystem.IsWindows();
        var mode = O.Upload ? FileMode.OpenOrCreate : FileMode.Open;
        var access = O.Upload ? FileAccess.Write : FileAccess.Read;
        Microsoft.Win32.SafeHandles.SafeFileHandle h;
        try { h = File.OpenHandle(O.Path, mode, access, FileShare.ReadWrite | FileShare.Delete, direct ? NoBuffering | (O.Upload ? FileOptions.WriteThrough : 0) : FileOptions.None); }
        catch (IOException) when (direct)
        {
            direct = false;
            h = File.OpenHandle(O.Path, mode, access, FileShare.ReadWrite | FileShare.Delete);
        }
        if (!direct)
            St.Note = OperatingSystem.IsWindows() || !O.Direct
                ? "buffered I/O: repeated reads may be served by the local cache"
                : $"no O_DIRECT on {Platform.Name}: buffered I/O, repeated reads may be served by the local cache";
        long block = (Math.Min(O.Block, 1L << 30) + 4095) & ~4095L;
        byte* buf = (byte*)NativeMemory.AlignedAlloc((nuint)block, 4096);
        try
        {
            using (h)
            {
                long size = O.Upload ? O.FileSize : RandomAccess.GetLength(h);
                if (size < block) throw new IOException($"{O.Path} is smaller than one block ({block} bytes); use --block");
                if (O.Upload) Buffer.MemoryCopy(B!.Ptr, buf, block, Math.Min(block, B.Size));
                var span = new Span<byte>(buf, (int)block);
                long stride = block * O.Connections, start = block * St.Index;
                long off = start;
                while (!Stop.IsCancellationRequested)
                {
                    if (off + block > size) off = start;
                    int n;
                    if (O.Upload) { RandomAccess.Write(h, (ReadOnlySpan<byte>)span, off); n = span.Length; }
                    else n = RandomAccess.Read(h, span, off);
                    if (n == 0) { off = start; continue; }
                    Count(n);
                    Interlocked.Increment(ref St.Requests);
                    off += stride;
                }
            }
        }
        finally { NativeMemory.AlignedFree(buf); }
    }
}
