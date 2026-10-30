namespace MemServe.Util;

/// <summary>
/// Hybrid-CPU awareness (Intel P/E cores). On an i7-13700K a TLS connection runs ~3x faster on a P-core than on an
/// E-core, so unpinned runs vary with where the scheduler puts each thread.
/// </summary>
public static class CpuInfo
{
    /// <summary>A warning when this process may run on both core types, else null (not hybrid, or already pinned to one type).</summary>
    public static string? HybridNote()
    {
        if (!OperatingSystem.IsLinux()) return null;
        var p = ReadList("/sys/devices/cpu_core/cpus");
        var e = ReadList("/sys/devices/cpu_atom/cpus");
        if (p.Count == 0 || e.Count == 0) return null;
        var allowed = AllowedCpus();
        if (allowed.Count > 0 && (!allowed.Overlaps(p) || !allowed.Overlaps(e))) return null;
        return $"hybrid CPU: P-cores {Ranges(p)}, E-cores {Ranges(e)}; E-cores are much slower for TLS. " +
               $"Pin to the P-cores for repeatable numbers (taskset -c {Ranges(p)}, or MEMSERVE_CPUSET={Ranges(p)} in docker compose)";
    }

    private static HashSet<int> AllowedCpus()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
                if (line.StartsWith("Cpus_allowed_list:")) return Parse(line[18..]);
        }
        catch (IOException) { }
        return [];
    }

    private static HashSet<int> ReadList(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>"0-7,16,18-19" -> {0..7,16,18,19}</summary>
    private static HashSet<int> Parse(string list)
    {
        var set = new HashSet<int>();
        foreach (var part in list.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var r = part.Split('-');
            if (!int.TryParse(r[0], out int a)) continue;
            int b = r.Length > 1 && int.TryParse(r[1], out int x) ? x : a;
            for (int i = a; i <= b; i++) set.Add(i);
        }
        return set;
    }

    private static string Ranges(HashSet<int> set)
    {
        var s = set.Order().ToList();
        var parts = new List<string>();
        for (int i = 0; i < s.Count;)
        {
            int j = i;
            while (j + 1 < s.Count && s[j + 1] == s[j] + 1) j++;
            parts.Add(i == j ? $"{s[i]}" : $"{s[i]}-{s[j]}");
            i = j + 1;
        }
        return string.Join(',', parts);
    }
}
