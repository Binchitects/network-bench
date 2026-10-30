using MemBench;

// `membench SERVER` (a host name, IP or host:port, nothing else) runs the whole built-in test plan;
// a URL or file path runs one test, as before
if (args.Length == 1 && IsServer(args[0])) return new Suite(args[0]).Run();

BenchOptions o;
try { o = BenchOptions.Parse(args); }
catch (Exception e) when (e is ArgumentException or FormatException or UriFormatException) { Console.Error.WriteLine(e.Message); return 2; }
return new Bench(o).Run();

static bool IsServer(string a) =>
    !a.StartsWith('-') && !a.Contains("://") && !a.Contains('/') && !a.Contains('\\') && !File.Exists(a) && !Directory.Exists(a);
