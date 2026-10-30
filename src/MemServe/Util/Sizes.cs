using System.Globalization;

namespace MemServe.Util;

public static class Sizes
{
    /// <summary>Parses "1G", "512M", "1.5GiB", "100MB", "4096" (bytes). Binary units (K = 1024).</summary>
    public static bool TryParse(string? text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        int i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (i == 0) return false;
        if (!double.TryParse(s.AsSpan(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) return false;
        var unit = s.AsSpan(i).Trim().ToString().ToUpperInvariant();
        if (unit.EndsWith("IB")) unit = unit[..^2];
        else if (unit.EndsWith("B") && unit.Length > 1) unit = unit[..^1];
        else if (unit == "B") unit = "";
        double mult = unit switch
        {
            "" => 1,
            "K" => 1L << 10,
            "M" => 1L << 20,
            "G" => 1L << 30,
            "T" => 1L << 40,
            "P" => 1L << 50,
            _ => -1,
        };
        if (mult < 0) return false;
        double v = num * mult;
        if (v < 0 || v > long.MaxValue) return false;
        bytes = (long)v;
        return true;
    }

    public static long Parse(string text)
        => TryParse(text, out var v) ? v : throw new FormatException($"Invalid size: '{text}'");

    public static string Human(double bytes)
    {
        string[] u = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        int i = 0;
        while (bytes >= 1024 && i < u.Length - 1) { bytes /= 1024; i++; }
        return i == 0 ? $"{bytes:0} {u[i]}" : $"{bytes:0.##} {u[i]}";
    }

    public static string Gbps(double bytesPerSecond) => $"{bytesPerSecond * 8 / 1e9:0.00} Gb/s";
}
