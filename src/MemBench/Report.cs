using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MemBench;

/// <summary>Writes a test run as report.html, report.md, results.csv and results.json into membench-results/&lt;time&gt;-&lt;host&gt;/.</summary>
internal static class Report
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Write(List<Scenario> plan, ServerInfo info, string target, DateTime started, TimeSpan took, string smb)
    {
        string safeHost = string.Concat(info.Host.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' ? ch : '_'));
        string dir = Path.Combine("membench-results", $"{started:yyyyMMdd-HHmmss}-{safeHost}");
        Directory.CreateDirectory(dir);
        var env = Environment(plan, info, target, started, took, smb);
        File.WriteAllText(Path.Combine(dir, "results.json"), Json(plan, env));
        File.WriteAllText(Path.Combine(dir, "results.csv"), Csv(plan));
        File.WriteAllText(Path.Combine(dir, "report.md"), Markdown(plan, env));
        File.WriteAllText(Path.Combine(dir, "report.html"), Html(plan, env));
        return dir;
    }

    private static List<(string Key, string Value)> Environment(List<Scenario> plan, ServerInfo i, string target, DateTime started, TimeSpan took, string smb)
    {
        var lc = plan.SelectMany(s => s.Cases).FirstOrDefault(c => c.Link != null);
        string? link = lc == null ? null : lc.Link == "lo" ? "same host (loopback): client and server share this machine's CPUs"
                                                          : $"{lc.Link}{(lc.LinkGbps is { } g ? $", {g:0.###} Gb/s" : "")}";
        return
        [
            ("Server", $"{target} - memserve {i.Version}, {i.Cpus} cpus"),
            ("Protocols", string.Join(", ", new[]
            {
                i.Http is { } h ? $"HTTP :{h}" : null,
                i.Https is { } s ? $"HTTPS :{s}" : null,
                i.Ftp is { } f ? $"FTP :{f}{(i.FtpTls ? " (+FTPS)" : "")}" : null,
            }.Where(x => x != null))),
            ("Server TLS", i.Https == null ? "off" : i.Ktls ? "kernel TLS (kTLS)" : $"user space ({i.TlsEngine})"),
            ("SMB", smb),
            ("Client", $"{RuntimeInformation.OSDescription.Trim()}, {System.Environment.ProcessorCount} cpus, {RuntimeInformation.ProcessArchitecture}, membench {typeof(Report).Assembly.GetName().Version?.ToString(3)}"),
            ("Network", link ?? "unknown"),
            ("Started", started.ToString("yyyy-MM-dd HH:mm:ss", Inv)),
            ("Duration", took.ToString(@"m\:ss", Inv)),
            ("Method", $"each case: 1 s warmup + 3 s measured, its own membench process; TLS certificates not verified"),
        ];
    }

    private static string Counts(List<Scenario> plan)
    {
        int C(string s) => plan.Sum(x => x.Cases.Count(c => c.Status == s));
        return $"{C("PASS")} passed, {C("WARN")} warnings, {C("FAIL")} failed, {C("SKIP")} skipped";
    }

    // ------------------------------------------------------------ JSON

    private static string Json(List<Scenario> plan, List<(string Key, string Value)> env)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartObject("environment");
            foreach (var (k, v) in env) w.WriteString(k.ToLowerInvariant().Replace(' ', '_'), v);
            w.WriteEndObject();
            w.WriteString("summary", Counts(plan));
            w.WriteStartArray("scenarios");
            foreach (var s in plan)
            {
                w.WriteStartObject();
                w.WriteString("name", s.Name);
                w.WriteString("description", s.Description);
                if (s.SkipReason != null) w.WriteString("skipped", s.SkipReason);
                w.WriteStartArray("cases");
                foreach (var c in s.Cases)
                {
                    w.WriteStartObject();
                    w.WriteString("name", c.Name);
                    w.WriteString("protocol", c.Protocol);
                    w.WriteString("direction", c.Direction);
                    w.WriteNumber("connections", c.Connections);
                    w.WriteString("status", c.Status);
                    w.WriteNumber("gbps", Math.Round(c.Gbps, 3));
                    w.WriteString("verdict", c.Verdict);
                    w.WriteStartArray("findings");
                    foreach (var f in c.Findings) w.WriteStringValue(f);
                    w.WriteEndArray();
                    w.WriteString("command", "membench " + string.Join(' ', c.Args));
                    if (c.RawJson != null) { w.WritePropertyName("measurement"); w.WriteRawValue(c.RawJson); }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ------------------------------------------------------------ CSV

    private static string Csv(List<Scenario> plan)
    {
        static string Q(string? v) => v == null ? "" : v.Contains(',') || v.Contains('"') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
        var sb = new StringBuilder("scenario,case,protocol,direction,connections,gbps,client_cores,server_cores,server_tls,verdict,status,findings\n");
        foreach (var s in plan)
            foreach (var c in s.Cases)
                sb.AppendJoin(',', Q(s.Name), Q(c.Name), c.Protocol, c.Direction, c.Connections.ToString(Inv), c.Gbps.ToString("0.000", Inv),
                    c.ClientCores.ToString("0.00", Inv), c.ServerCores.ToString("0.00", Inv), Q(c.ServerTls), Q(c.Verdict), c.Status, Q(string.Join("; ", c.Findings))).Append('\n');
        return sb.ToString();
    }

    // ------------------------------------------------------------ Markdown

    private static string Markdown(List<Scenario> plan, List<(string Key, string Value)> env)
    {
        var sb = new StringBuilder();
        sb.Append("# membench test report\n\n**").Append(Counts(plan)).Append("**\n\n");
        foreach (var (k, v) in env) sb.Append("- **").Append(k).Append(":** ").Append(v).Append('\n');
        foreach (var s in plan)
        {
            sb.Append("\n## ").Append(s.Name).Append("\n\n").Append(s.Description).Append(".\n\n");
            if (s.SkipReason != null) { sb.Append("Skipped: ").Append(s.SkipReason).Append("\n"); continue; }
            sb.Append("| Case | Conns | Gb/s | Client cores | Server cores | Verdict | Status |\n|---|---|---|---|---|---|---|\n");
            foreach (var c in s.Cases)
                sb.Append($"| {c.Name} | {c.Connections} | {(c.Status == "SKIP" ? "-" : c.Gbps.ToString("0.00", Inv))} | {c.ClientCores.ToString("0.0", Inv)} | {c.ServerCores.ToString("0.0", Inv)} | {c.Verdict ?? "-"} | {c.Status} |\n");
            var notes = s.Cases.Where(c => c.Findings.Count > 0).ToList();
            if (notes.Count > 0)
            {
                sb.Append('\n');
                foreach (var c in notes) sb.Append("- **").Append(c.Name).Append(" (").Append(c.Status).Append("):** ").Append(string.Join("; ", c.Findings)).Append('\n');
            }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ HTML

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Html(List<Scenario> plan, List<(string Key, string Value)> env)
    {
        int C(string st) => plan.Sum(x => x.Cases.Count(c => c.Status == st));
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>membench report</title>
<style>
:root{--bg:#f7f7f5;--card:#fff;--text:#1d1d1b;--muted:#6b6b66;--line:#e4e4df;--bar:#3d6fd8;--pass:#1f8a4c;--warn:#b7791f;--fail:#c53030;--skip:#8a8a85}
@media (prefers-color-scheme:dark){:root{--bg:#161615;--card:#1f1f1d;--text:#ececea;--muted:#9a9a94;--line:#33332f;--bar:#6b95ef;--pass:#4cc38a;--warn:#e0a84a;--fail:#f07070;--skip:#7d7d78}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 system-ui,-apple-system,"Segoe UI",sans-serif}
main{max-width:1100px;margin:0 auto;padding:24px 16px 48px}h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:0}
.sub{color:var(--muted);margin:0 0 16px}.chips{display:flex;gap:8px;flex-wrap:wrap;margin:12px 0 20px}
.chip{padding:6px 12px;border-radius:999px;background:var(--card);border:1px solid var(--line);font-weight:600}
.chip b{font-size:16px;margin-right:4px}.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px;margin:0 0 16px;overflow-x:auto}
dl{display:grid;grid-template-columns:max-content 1fr;gap:4px 16px;margin:0}dt{color:var(--muted)}dd{margin:0}
table{width:100%;border-collapse:collapse;margin-top:10px;min-width:720px}th,td{text-align:left;padding:7px 8px;border-top:1px solid var(--line);vertical-align:top}
th{color:var(--muted);font-weight:500;font-size:12px;border-top:0}td.n{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}
.barwrap{display:flex;align-items:center;gap:8px;min-width:220px;white-space:nowrap}.bar{height:10px;border-radius:5px;background:var(--bar);min-width:2px}
.badge{display:inline-block;padding:1px 8px;border-radius:999px;font-size:12px;font-weight:700;color:#fff}
.PASS{background:var(--pass)}.WARN{background:var(--warn)}.FAIL{background:var(--fail)}.SKIP{background:var(--skip)}
.find{color:var(--muted);font-size:12px;margin-top:3px}.find.WARN{color:var(--warn);background:none}.find.FAIL{color:var(--fail);background:none}
.desc{color:var(--muted);margin:2px 0 0}code{font-size:12px}
</style></head><body><main>
<h1>membench test report</h1>
""");
        sb.Append($"<p class=\"sub\">{H(env.First(e => e.Key == "Server").Value)} &middot; {H(env.First(e => e.Key == "Started").Value)}</p>");
        sb.Append("<div class=\"chips\">");
        foreach (var (st, label) in new[] { ("PASS", "passed"), ("WARN", "warnings"), ("FAIL", "failed"), ("SKIP", "skipped") })
            sb.Append($"<span class=\"chip\"><b>{C(st)}</b><span class=\"badge {st}\">{label}</span></span>");
        sb.Append("</div><section class=\"card\"><dl>");
        foreach (var (k, v) in env) sb.Append($"<dt>{H(k)}</dt><dd>{H(v)}</dd>");
        sb.Append("</dl></section>");

        foreach (var s in plan)
        {
            double max = Math.Max(s.Cases.Select(c => c.Gbps).DefaultIfEmpty(0).Max(), 1e-9);
            sb.Append($"<section class=\"card\"><h2>{H(s.Name)}</h2><p class=\"desc\">{H(s.Description)}</p>");
            if (s.SkipReason != null) { sb.Append($"<p class=\"find\">Skipped: {H(s.SkipReason)}</p></section>"); continue; }
            sb.Append("<table><tr><th>Case</th><th>Conns</th><th>Throughput</th><th>Client cores</th><th>Server cores</th><th>Verdict</th><th>Status</th></tr>");
            foreach (var c in s.Cases)
            {
                string bar = c.Status == "SKIP" ? "-" :
                    $"<div class=\"barwrap\"><div class=\"bar\" style=\"width:{(c.Gbps / max * 160).ToString("0", Inv)}px\"></div><span>{c.Gbps.ToString("0.00", Inv)} Gb/s</span></div>";
                sb.Append($"<tr><td>{H(c.Name)}");
                foreach (var f in c.Findings) sb.Append($"<div class=\"find {c.Status}\">{H(f)}</div>");
                if (c.Reason != null && c.Status != "SKIP") sb.Append($"<div class=\"find\">{H(c.Reason)}</div>");
                sb.Append($"</td><td class=\"n\">{c.Connections}</td><td>{bar}</td>" +
                          $"<td class=\"n\">{(c.Status == "SKIP" ? "-" : c.ClientCores.ToString("0.0", Inv))}</td>" +
                          $"<td class=\"n\">{(c.Status == "SKIP" ? "-" : c.ServerCores.ToString("0.0", Inv))}</td>" +
                          $"<td>{H(c.Verdict ?? "-")}</td><td><span class=\"badge {c.Status}\">{c.Status}</span></td></tr>");
            }
            sb.Append("</table></section>");
        }
        sb.Append("<p class=\"find\">Re-run any case on its own with the command in results.json, without --json, to see its per-second numbers and full verdict.</p>");
        sb.Append("</main></body></html>\n");
        return sb.ToString();
    }
}
