using System.Collections.Concurrent;
using MemServe.Util;

namespace MemServe.Core;

/// <summary>A file visible to all three protocols. Pattern files are read-only and cost no memory; sink files swallow uploads.</summary>
public sealed class VEntry
{
    private static long _nextId = 0x1000;
    public string Name;
    private long _size;
    public long Size { get => Interlocked.Read(ref _size); set => Interlocked.Exchange(ref _size, value); }
    public readonly bool IsSink;
    /// <summary>True for on-demand &lt;size&gt;.bin files that are not in the directory listing.</summary>
    public readonly bool Transient;
    public DateTime Created, Modified;
    public readonly ulong Id = (ulong)Interlocked.Increment(ref _nextId);

    public VEntry(string name, long size, bool isSink, bool transient, DateTime time)
    {
        Name = name; _size = size; IsSink = isSink; Transient = transient; Created = Modified = time;
    }

    /// <summary>Grows a sink file; returns false for pattern files.</summary>
    public bool Extend(long endOffset)
    {
        if (!IsSink) return false;
        long cur;
        while ((cur = Size) < endOffset && Interlocked.CompareExchange(ref _size, endOffset, cur) != cur) { }
        Modified = DateTime.UtcNow;
        return true;
    }
}

public sealed class VirtualFs
{
    private readonly ConcurrentDictionary<string, VEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    public readonly DateTime StartTime = DateTime.UtcNow;
    public readonly Backing Backing;

    public VirtualFs(Backing backing, IEnumerable<string> sizeTokens)
    {
        Backing = backing;
        foreach (var tok in sizeTokens)
        {
            if (!Sizes.TryParse(tok, out var size)) throw new ArgumentException($"Invalid file size '{tok}' in --files");
            string name = tok.ToUpperInvariant() + ".bin";
            _entries[name] = new VEntry(name, size, false, false, StartTime);
        }
    }

    public IEnumerable<VEntry> List() => _entries.Values.OrderBy(e => e.IsSink).ThenBy(e => e.Size).ThenBy(e => e.Name);

    /// <summary>Resolves a name. Unknown names that parse as a size (e.g. "25G.bin", "512M", "1.5T") become transient pattern files.</summary>
    public bool TryGet(string name, out VEntry entry)
    {
        name = Normalize(name);
        if (_entries.TryGetValue(name, out entry!)) return true;
        string stem = name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (stem.Length > 0 && stem.Length <= 24 && Sizes.TryParse(stem, out var size) && size >= 0)
        {
            entry = new VEntry(stem.ToUpperInvariant() + ".bin", size, false, true, StartTime);
            return true;
        }
        entry = null!;
        return false;
    }

    public VEntry CreateSink(string name)
    {
        name = Normalize(name);
        return _entries.AddOrUpdate(name,
            n => new VEntry(n, 0, true, false, DateTime.UtcNow),
            (n, old) => old.IsSink ? Reset(old) : old);
        static VEntry Reset(VEntry e) { e.Size = 0; e.Modified = DateTime.UtcNow; return e; }
    }

    public bool Remove(string name) => _entries.TryGetValue(Normalize(name), out var e) && e.IsSink && _entries.TryRemove(e.Name, out _);

    public bool Rename(VEntry e, string newName, bool replace)
    {
        newName = Normalize(newName);
        if (!e.IsSink || newName.Length == 0) return false;
        if (_entries.TryGetValue(newName, out var existing) && existing != e)
        {
            if (!replace || !existing.IsSink) return false;
            _entries.TryRemove(newName, out _);
        }
        _entries.TryRemove(e.Name, out _);
        e.Name = newName;
        _entries[newName] = e;
        return true;
    }

    public static string Normalize(string name)
    {
        name = name.Replace('\\', '/').Trim();
        while (name.StartsWith('/')) name = name[1..];
        if (name.StartsWith("./")) name = name[2..];
        return name;
    }

    public static bool IsValidNewName(string name)
        => name.Length is > 0 and < 200 && !name.Contains('/') && !name.Contains('\\') && name is not ("." or "..");
}
