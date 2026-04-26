using PsxInject.Server;

namespace PsxInject.Models;

public class CachedFile
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public long Size { get; init; }
    public DateTime LastModified { get; init; }

    /// <summary>
    /// True when the file actually exists in the data folder.
    /// False for entries we know about only because we've seen the PS4 request
    /// them (e.g., they're being proxied through Sony fallback right now).
    /// </summary>
    public bool IsCached { get; init; } = true;

    /// <summary>
    /// Last-known absolute URL for this filename — copy-pasteable into a
    /// download manager. Empty string for actually-cached files (no source URL).
    /// </summary>
    public string SourceUrl { get; init; } = "";

    public bool HasSourceUrl => !string.IsNullOrWhiteSpace(SourceUrl);

    public string SizeText => Size > 0 ? FormatHelpers.FormatBytes(Size) : "—";
    public string ModifiedText => IsCached ? LastModified.ToString("yyyy-MM-dd HH:mm") : "in flight";
}
