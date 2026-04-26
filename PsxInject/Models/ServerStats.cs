namespace PsxInject.Models;

public class ServerStats
{
    private long _totalRequests;
    private long _cacheHits;
    private long _cacheMisses;
    private long _proxyRequests;
    private long _errors;
    private long _bytesServed;

    public DateTime StartTime { get; private set; } = DateTime.Now;

    public long TotalRequests => Interlocked.Read(ref _totalRequests);
    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public long CacheMisses => Interlocked.Read(ref _cacheMisses);
    public long ProxyRequests => Interlocked.Read(ref _proxyRequests);
    public long Errors => Interlocked.Read(ref _errors);
    public long BytesServed => Interlocked.Read(ref _bytesServed);

    public void IncTotal() => Interlocked.Increment(ref _totalRequests);
    public void IncHit() => Interlocked.Increment(ref _cacheHits);
    public void IncMiss() => Interlocked.Increment(ref _cacheMisses);
    public void IncProxy() => Interlocked.Increment(ref _proxyRequests);
    public void IncError() => Interlocked.Increment(ref _errors);
    public void AddBytes(long n) => Interlocked.Add(ref _bytesServed, n);

    public void Reset()
    {
        Interlocked.Exchange(ref _totalRequests, 0);
        Interlocked.Exchange(ref _cacheHits, 0);
        Interlocked.Exchange(ref _cacheMisses, 0);
        Interlocked.Exchange(ref _proxyRequests, 0);
        Interlocked.Exchange(ref _errors, 0);
        Interlocked.Exchange(ref _bytesServed, 0);
        StartTime = DateTime.Now;
    }

    public TimeSpan Uptime => DateTime.Now - StartTime;
}
