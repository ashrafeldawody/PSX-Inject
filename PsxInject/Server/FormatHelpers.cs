namespace PsxInject.Server;

public static class FormatHelpers
{
    private static readonly string[] Sizes = { "B", "KB", "MB", "GB", "TB" };

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < Sizes.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return $"{v:0.##} {Sizes[i]}";
    }

    public static string FormatUptime(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        return $"{span.Seconds}s";
    }
}
