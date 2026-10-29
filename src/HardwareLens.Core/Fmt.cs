using System.Globalization;
namespace HardwareLens.Core;

/// <summary>Number formatting with explicit, unambiguous units. Binary units (KiB/MiB/GiB) for memory and throughput; decimal for storage marketing sizes and bit rates.</summary>
public static class Fmt
{
    static readonly CultureInfo C = CultureInfo.InvariantCulture;
    public const string Dash = "—";

    public static string Bytes(double? bytes, int digits = 1)
    {
        if (bytes is null || double.IsNaN(bytes.Value)) return Dash;
        double v = bytes.Value; string[] u = { "B", "KiB", "MiB", "GiB", "TiB", "PiB" }; int i = 0;
        while (Math.Abs(v) >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return (i == 0 ? v.ToString("0", C) : v.ToString("F" + digits, C)) + " " + u[i];
    }

    /// <summary>Bytes per second as MiB/s etc.</summary>
    public static string Throughput(double? bytesPerSec)
    {
        if (bytesPerSec is null || double.IsNaN(bytesPerSec.Value)) return Dash;
        return Bytes(bytesPerSec, 1) + "/s";
    }

    /// <summary>Bytes per second converted to megabits per second (decimal), as used for network link rates.</summary>
    public static string Mbit(double? bytesPerSec)
    {
        if (bytesPerSec is null || double.IsNaN(bytesPerSec.Value)) return Dash;
        double mbit = bytesPerSec.Value * 8 / 1e6;
        return (mbit >= 100 ? mbit.ToString("F0", C) : mbit >= 10 ? mbit.ToString("F1", C) : mbit.ToString("F2", C)) + " Mbit/s";
    }

    public static string LinkSpeed(ulong? bitsPerSec)
    {
        if (bitsPerSec is null || bitsPerSec == 0 || bitsPerSec == ulong.MaxValue) return Dash;
        double b = bitsPerSec.Value;
        if (b >= 1e9) return (b / 1e9).ToString("0.##", C) + " Gbit/s";
        return (b / 1e6).ToString("0.##", C) + " Mbit/s";
    }

    /// <summary>Drive capacity as the vendor's decimal size plus Windows-style binary size, e.g. "1 TB (931.5 GiB)".</summary>
    public static string Capacity(ulong bytes)
    {
        if (bytes == 0) return Dash;
        double dec = bytes >= 1e12 ? bytes / 1e12 : bytes / 1e9;
        string unit = bytes >= 1e12 ? "TB" : "GB";
        string d = (dec >= 100 ? Math.Round(dec).ToString("0", C) : Math.Round(dec, 2).ToString("0.##", C)) + " " + unit;
        return d + " (" + Bytes(bytes) + ")";
    }

    public static string Percent(double? v, int digits = 0)
        => v is null || double.IsNaN(v.Value) ? Dash : v.Value.ToString("F" + digits, C) + "%";

    public static string Duration(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} d {t.Hours} h {t.Minutes} min";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} min {t.Seconds} s";
        return $"{(int)t.TotalSeconds} s";
    }

    public static string Num(double? v, string format = "0.#", string unit = "")
        => v is null || double.IsNaN(v.Value) ? Dash : v.Value.ToString(format, C) + unit;

    public static string Mhz(double? mhz)
        => mhz is null || double.IsNaN(mhz.Value) ? Dash : mhz.Value >= 1000 ? (mhz.Value / 1000).ToString("0.00", C) + " GHz" : mhz.Value.ToString("0", C) + " MHz";
}
