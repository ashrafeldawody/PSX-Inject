namespace PsxInject.Models;

public enum RequestKind
{
    CacheHit,
    CacheMiss,
    Proxy,
    Fallback,   // .pkg miss that was forwarded to Sony servers (when AllowSonyFallback = true)
    Error
}

public class RequestLogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public RequestKind Kind { get; init; }
    public string Method { get; init; } = "GET";
    public string Url { get; init; } = "";
    public string FileName { get; init; } = "";
    public int StatusCode { get; init; }
    public long Bytes { get; init; }
    public string Detail { get; init; } = "";

    public string TimeText => Timestamp.ToString("HH:mm:ss");

    public string KindText => Kind switch
    {
        RequestKind.CacheHit => "HIT",
        RequestKind.CacheMiss => "MISS",
        RequestKind.Proxy => "PROXY",
        RequestKind.Fallback => "SONY",
        RequestKind.Error => "ERROR",
        _ => "?"
    };

    /// <summary>True for any row with a real URL we can copy to the clipboard.</summary>
    public bool IsCopyable =>
        !string.IsNullOrEmpty(Url) &&
        (Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}
