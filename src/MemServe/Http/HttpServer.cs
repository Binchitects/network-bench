using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MemServe.Core;
using MemServe.Util;

namespace MemServe.Http;

/// <summary>
/// Minimal, fast HTTP/1.1 server: GET/HEAD with ranges, PUT/POST upload sink, keep-alive, pipelining.
/// Plain HTTP is zero-copy (sendfile / MSG_TRUNC); HTTPS runs the same code over TLS when tls is given.
/// </summary>
public sealed class HttpServer(Options opt, VirtualFs fs, Stats stats, Tls? tls = null)
{
    private readonly ProtoStats _st = tls == null ? stats.Http : stats.Https;
    private static readonly byte[] CrLfCrLf = "\r\n\r\n"u8.ToArray();

    public void Start()
    {
        if (tls == null)
        {
            Wire.AcceptLoop(Wire.Listen(opt.Bind, opt.HttpPort), "http", s => Handle(Conn.Plain(s)));
            return;
        }
        Wire.AcceptLoop(Wire.Listen(opt.Bind, opt.HttpsPort), "https", s =>
        {
            s.ReceiveTimeout = 30_000; // bounds the handshake
            using var c = tls.Accept(s, http: true);
            Handle(c);
        });
    }

    private sealed class Request
    {
        public string Method = "", Target = "", Version = "";
        public long ContentLength = -1;
        public bool Chunked, Expect100, Close, KeepAlive;
        public string? Range;
    }

    private void Handle(Conn s)
    {
        _st.Connected();
        try
        {
            s.Socket.ReceiveTimeout = 120_000;
            s.Socket.SendTimeout = 120_000;
            var buf = new byte[64 * 1024];
            int have = 0;
            while (true)
            {
                // ---- read head
                int headEnd;
                while ((headEnd = IndexOf(buf, have, CrLfCrLf)) < 0)
                {
                    if (have == buf.Length) { SendSimple(s, 431, "Request Header Fields Too Large", close: true); return; }
                    int n;
                    try { n = s.Receive(buf.AsSpan(have)); }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut) { return; }
                    if (n <= 0) return;
                    have += n;
                }
                int headLen = headEnd + 4;
                var req = ParseHead(Encoding.Latin1.GetString(buf, 0, headEnd));
                if (req == null) { SendSimple(s, 400, "Bad Request", close: true); return; }

                // leftover after the head = start of body (or next pipelined request)
                int bodyStart = headLen;
                int leftover = have - headLen;
                bool keep = !req.Close && (req.Version == "HTTP/1.1" || req.KeepAlive);

                long consumedBody = 0;
                bool ok = Dispatch(s, req, buf, bodyStart, leftover, keep, out consumedBody);
                if (!ok) return;

                // ---- shift leftover (bytes after the consumed body) to the front for pipelining
                int bodyInBuf = (int)Math.Min(consumedBody, leftover);
                int rest = leftover - bodyInBuf;
                if (rest > 0) Buffer.BlockCopy(buf, bodyStart + bodyInBuf, buf, 0, rest);
                have = rest;
                if (!keep) return;
            }
        }
        finally { _st.Disconnected(); }
    }

    private static int IndexOf(byte[] buf, int len, byte[] pat)
    {
        int idx = buf.AsSpan(0, len).IndexOf(pat);
        return idx;
    }

    private static Request? ParseHead(string head)
    {
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ', 3);
        if (parts.Length < 3) return null;
        var r = new Request { Method = parts[0].ToUpperInvariant(), Target = parts[1], Version = parts[2] };
        for (int i = 1; i < lines.Length; i++)
        {
            int c = lines[i].IndexOf(':');
            if (c <= 0) continue;
            string name = lines[i][..c].Trim(), val = lines[i][(c + 1)..].Trim();
            switch (name.ToLowerInvariant())
            {
                case "content-length": if (long.TryParse(val, out var cl)) r.ContentLength = cl; break;
                case "transfer-encoding": r.Chunked = val.Contains("chunked", StringComparison.OrdinalIgnoreCase); break;
                case "expect": r.Expect100 = val.Contains("100-continue", StringComparison.OrdinalIgnoreCase); break;
                case "connection":
                    if (val.Contains("close", StringComparison.OrdinalIgnoreCase)) r.Close = true;
                    if (val.Contains("keep-alive", StringComparison.OrdinalIgnoreCase)) r.KeepAlive = true;
                    break;
                case "range": r.Range = val; break;
            }
        }
        return r;
    }

    /// <summary>Returns false when the connection must be dropped. consumedBody = body bytes that belonged to this request.</summary>
    private bool Dispatch(Conn s, Request req, byte[] buf, int bodyStart, int leftover, bool keep, out long consumedBody)
    {
        consumedBody = 0;
        string path = req.Target;
        int q = path.IndexOf('?'); if (q >= 0) path = path[..q];
        path = Uri.UnescapeDataString(path);
        string name = VirtualFs.Normalize(path);

        switch (req.Method)
        {
            case "GET":
            case "HEAD":
            {
                bool head = req.Method == "HEAD";
                // a GET with a body is unusual; swallow it to keep the stream in sync
                if (req.ContentLength > 0) { consumedBody = DrainBody(s, req, buf, bodyStart, leftover); }
                else if (req.Chunked) { consumedBody = DrainBody(s, req, buf, bodyStart, leftover); }

                if (name is "" or "index.html") return SendText(s, 200, "text/html; charset=utf-8", IndexHtml(), keep, head);
                if (name == "stats") return SendText(s, 200, "application/json", stats.ToJson(), keep, head);
                if (name == "health") return SendText(s, 200, "text/plain", "ok\n", keep, head);
                if (name == "favicon.ico") return SendSimple(s, 404, "Not Found", close: !keep);
                if (!fs.TryGet(name, out var e)) return SendSimple(s, 404, "Not Found", close: !keep);
                return SendFile(s, e, req, keep, head);
            }
            case "PUT":
            case "POST":
            case "PATCH":
            {
                if (req.Expect100) s.SendAll("HTTP/1.1 100 Continue\r\n\r\n"u8);
                var sw = Stopwatch.StartNew();
                long bytes;
                if (req.Chunked) bytes = DrainChunked(s, buf, bodyStart, leftover, out consumedBody);
                else if (req.ContentLength >= 0) { bytes = DrainFixed(s, req.ContentLength, buf, bodyStart, leftover); consumedBody = req.ContentLength; }
                else { bytes = DrainFixed(s, long.MaxValue, buf, bodyStart, leftover); consumedBody = bytes; keep = false; }
                double sec = sw.Elapsed.TotalSeconds;
                _st.Transfer();
                if (req.Method == "PUT" && VirtualFs.IsValidNewName(name))
                {
                    var sink = fs.CreateSink(name);
                    sink.Extend(bytes);
                }
                string json = $"{{\"bytes\":{bytes},\"seconds\":{sec.ToString("0.000", CultureInfo.InvariantCulture)},\"gbps\":{(bytes * 8 / 1e9 / Math.Max(sec, 1e-9)).ToString("0.00", CultureInfo.InvariantCulture)},\"MBps\":{(bytes / 1e6 / Math.Max(sec, 1e-9)).ToString("0.0", CultureInfo.InvariantCulture)}}}\n";
                return SendText(s, 200, "application/json", json, keep, false);
            }
            case "DELETE":
                return fs.Remove(name) ? SendSimple(s, 204, "No Content", close: !keep) : SendSimple(s, 404, "Not Found", close: !keep);
            case "OPTIONS":
                return SendRaw(s, $"HTTP/1.1 204 No Content\r\nAllow: GET, HEAD, PUT, POST, DELETE, OPTIONS\r\nAccept-Ranges: bytes\r\nContent-Length: 0\r\nConnection: {(keep ? "keep-alive" : "close")}\r\n\r\n", keep);
            default:
                return SendSimple(s, 405, "Method Not Allowed", close: !keep);
        }
    }

    private long DrainBody(Conn s, Request req, byte[] buf, int bodyStart, int leftover)
    {
        if (req.Chunked) { DrainChunked(s, buf, bodyStart, leftover, out var consumed); return consumed; }
        DrainFixed(s, req.ContentLength, buf, bodyStart, leftover);
        return req.ContentLength;
    }

    /// <summary>Discards exactly `length` body bytes (or until EOF when long.MaxValue). Returns bytes seen.</summary>
    private long DrainFixed(Conn s, long length, byte[] buf, int bodyStart, int leftover)
    {
        long inBuf = Math.Min(length, leftover);
        _st.AddIn(inBuf);
        long rest = length == long.MaxValue ? long.MaxValue : length - inBuf;
        long got = inBuf;
        if (rest > 0) got += s.Discard(rest, _st);
        return got;
    }

    /// <summary>Parses chunked transfer coding and discards it. consumed = raw bytes belonging to this body.</summary>
    private long DrainChunked(Conn s, byte[] buf, int bodyStart, int leftover, out long consumed)
    {
        // work on a private window; whatever is left in `buf` beyond the body is handled by the caller via `consumed`
        var window = new byte[64 * 1024];
        int have = Math.Min(leftover, window.Length);
        Buffer.BlockCopy(buf, bodyStart, window, 0, have);
        int extraFromBuf = leftover - have; // cannot happen (leftover <= 64K) but keep correct
        long raw = 0, payload = 0;
        int pos = 0;
        while (true)
        {
            // read a chunk-size line
            int lineEnd;
            while ((lineEnd = window.AsSpan(pos, have - pos).IndexOf("\r\n"u8)) < 0)
            {
                if (pos > 0) { Buffer.BlockCopy(window, pos, window, 0, have - pos); have -= pos; pos = 0; }
                if (have == window.Length) throw new IOException("chunk line too long");
                int n = s.Receive(window.AsSpan(have));
                if (n <= 0) throw new IOException("eof in chunked body");
                have += n;
            }
            string line = Encoding.Latin1.GetString(window, pos, lineEnd);
            int semi = line.IndexOf(';'); if (semi >= 0) line = line[..semi];
            long size = long.Parse(line.Trim(), NumberStyles.HexNumber);
            pos += lineEnd + 2; raw += lineEnd + 2;
            if (size == 0)
            {
                // trailers until empty line
                while (true)
                {
                    int te;
                    while ((te = window.AsSpan(pos, have - pos).IndexOf("\r\n"u8)) < 0)
                    {
                        if (pos > 0) { Buffer.BlockCopy(window, pos, window, 0, have - pos); have -= pos; pos = 0; }
                        int n = s.Receive(window.AsSpan(have));
                        if (n <= 0) throw new IOException("eof in trailers");
                        have += n;
                    }
                    pos += te + 2; raw += te + 2;
                    if (te == 0) break;
                }
                consumed = Math.Min(raw, leftover);
                _st.AddIn(payload);
                return payload;
            }
            // chunk payload + CRLF
            long need = size + 2;
            long inWin = Math.Min(need, have - pos);
            pos += (int)inWin; raw += inWin; need -= inWin;
            if (need > 0)
            {
                // beyond the window: everything from the socket from here on belongs to this body
                have = 0; pos = 0;
                long got = s.Discard(need, new ProtoStats("tmp"));
                if (got < need) throw new IOException("eof in chunk");
                raw += need;
            }
            payload += size;
        }
    }

    private bool SendFile(Conn s, VEntry e, Request req, bool keep, bool head)
    {
        long size = e.Size, start = 0, end = size - 1;
        int status = 200;
        if (req.Range != null && req.Range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) && !req.Range.Contains(','))
        {
            var r = req.Range[6..].Trim();
            int dash = r.IndexOf('-');
            if (dash < 0) return SendRangeNotSatisfiable(s, size, keep);
            string a = r[..dash].Trim(), b = r[(dash + 1)..].Trim();
            if (a.Length == 0)
            {
                if (!long.TryParse(b, out var suffix) || suffix <= 0) return SendRangeNotSatisfiable(s, size, keep);
                start = Math.Max(0, size - suffix); end = size - 1;
            }
            else
            {
                if (!long.TryParse(a, out start)) return SendRangeNotSatisfiable(s, size, keep);
                if (b.Length > 0) { if (!long.TryParse(b, out end)) return SendRangeNotSatisfiable(s, size, keep); }
                else end = size - 1;
                if (end >= size) end = size - 1;
                if (start >= size || start > end) return SendRangeNotSatisfiable(s, size, keep);
            }
            status = 206;
        }
        long count = size == 0 ? 0 : end - start + 1;
        var sb = new StringBuilder(256);
        sb.Append(status == 206 ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        sb.Append("Server: memserve\r\nDate: ").Append(DateTime.UtcNow.ToString("R")).Append("\r\n");
        sb.Append("Content-Type: application/octet-stream\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\n");
        sb.Append("Content-Disposition: attachment; filename=\"").Append(e.Name).Append("\"\r\n");
        sb.Append("Content-Length: ").Append(count).Append("\r\n");
        if (status == 206) sb.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(size).Append("\r\n");
        sb.Append("Connection: ").Append(keep ? "keep-alive" : "close").Append("\r\n\r\n");
        var hdr = Encoding.ASCII.GetBytes(sb.ToString());
        if (head || count == 0) { s.SendAll(hdr); return true; }
        _st.Transfer();
        s.SendPattern(fs.Backing, start, count, _st, hdr);
        return true;
    }

    private bool SendRangeNotSatisfiable(Conn s, long size, bool keep)
        => SendRaw(s, $"HTTP/1.1 416 Range Not Satisfiable\r\nServer: memserve\r\nContent-Range: bytes */{size}\r\nContent-Length: 0\r\nConnection: {(keep ? "keep-alive" : "close")}\r\n\r\n", keep);

    private static bool SendText(Conn s, int status, string ctype, string body, bool keep, bool head)
    {
        var b = Encoding.UTF8.GetBytes(body);
        string reason = status switch { 200 => "OK", 404 => "Not Found", _ => "OK" };
        var h = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nServer: memserve\r\nDate: {DateTime.UtcNow:R}\r\nContent-Type: {ctype}\r\nContent-Length: {b.Length}\r\nCache-Control: no-store\r\nConnection: {(keep ? "keep-alive" : "close")}\r\n\r\n");
        s.SendAll(h);
        if (!head) s.SendAll(b);
        return true;
    }

    private static bool SendSimple(Conn s, int status, string reason, bool close)
    {
        var body = Encoding.ASCII.GetBytes(reason + "\n");
        var h = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nServer: memserve\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: {(close ? "close" : "keep-alive")}\r\n\r\n");
        s.SendAll(h); s.SendAll(body);
        return !close;
    }

    private static bool SendRaw(Conn s, string head, bool keep) { s.SendAll(Encoding.ASCII.GetBytes(head)); return keep; }

    private string IndexHtml()
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=utf-8><title>memserve</title><style>body{font-family:system-ui,sans-serif;max-width:900px;margin:2em auto;padding:0 1em;color:#222}table{border-collapse:collapse}td,th{padding:4px 12px;border-bottom:1px solid #ddd;text-align:left}code,pre{background:#f4f4f4;padding:2px 5px;border-radius:3px}pre{padding:10px;overflow:auto}</style></head><body>");
        sb.Append("<h1>memserve</h1><p>In-memory benchmark file server. Files are generated from a ").Append(Sizes.Human(fs.Backing.Size)).Append(" random pattern held in RAM and sent with zero copies; nothing is stored on disk.</p>");
        sb.Append("<table><tr><th>File</th><th>Size</th><th>Bytes</th></tr>");
        foreach (var e in fs.List())
            sb.Append("<tr><td><a href=\"/").Append(e.Name).Append("\">").Append(e.Name).Append(e.IsSink ? " (uploaded)" : "").Append("</a></td><td>").Append(Sizes.Human(e.Size)).Append("</td><td>").Append(e.Size).Append("</td></tr>");
        sb.Append("</table><p>Any <code>/&lt;size&gt;</code> works on demand, e.g. <a href=\"/5G\">/5G</a>, <a href=\"/512M\">/512M</a>, <a href=\"/2.5T.bin\">/2.5T.bin</a>. Range requests are supported (multi-connection downloaders work).</p>");
        sb.Append("<h2>Examples</h2><pre>");
        sb.Append("# download speed (single stream)\ncurl -o /dev/null http://HOST/10G\n\n# 8 parallel streams\naria2c -x8 -s8 -d /dev --allow-overwrite=true -o null http://HOST/10G\n\n# upload speed (data is discarded, result is JSON)\ncurl -T /dev/zero -H 'Content-Length: 10737418240' http://HOST/upload\nhead -c 10G /dev/zero | curl -X POST --data-binary @- http://HOST/upload\n\n# stats\ncurl http://HOST/stats\n");
        sb.Append("</pre><p>FTP: <code>ftp://HOST/</code> (any user/password)</p></body></html>");
        return sb.ToString();
    }
}
