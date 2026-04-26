using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using PsxInject.Models;

namespace PsxInject.Server;

public class ProxyServer : IDisposable
{
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Proxy-Connection", "Keep-Alive", "Transfer-Encoding",
        "TE", "Trailer", "Upgrade", "Proxy-Authorization", "Proxy-Authenticate"
    };

    private readonly HttpClient _http;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public ServerStats Stats { get; } = new();

    public bool IsRunning { get; private set; }
    public int Port { get; private set; }
    public string DataDirectory { get; private set; } = "";
    public int RequestTimeoutMs { get; set; } = 30_000;

    /// <summary>
    /// When true, .pkg requests not found in the cache are forwarded to Sony's
    /// servers (so the PS4 can finish the download through this proxy).
    /// When false, missing files return 404 — the user must place the file manually.
    /// </summary>
    public bool AllowSonyFallback { get; set; } = false;

    public event Action<RequestLogEntry>? RequestLogged;
    public event Action<string, bool>? Status; // message, isError

    public ProxyServer()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        _http = new HttpClient(handler, disposeHandler: true);
    }

    public Task StartAsync(int port, string dataDirectory)
    {
        if (IsRunning) throw new InvalidOperationException("Server already running.");

        Directory.CreateDirectory(dataDirectory);

        // Authoritative probe: do NetworkService.IsPortAvailable's checks here too
        // (active TCP connect to localhost + v4/v6 loopback + wildcard probe-binds).
        // If the port looks taken, refuse to start.
        if (!Services.NetworkService.IsPortAvailable(port))
        {
            throw new PortInUseException(port);
        }

        var listener = new TcpListener(IPAddress.Any, port)
        {
            // Force-fail the bind if another process already owns the port.
            // Without this, Windows can silently let the bind through and route
            // traffic to whoever got there first.
            ExclusiveAddressUse = true
        };

        try
        {
            listener.Start(backlog: 64);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            try { listener.Stop(); } catch { }
            throw new PortInUseException(port, ex);
        }
        catch
        {
            try { listener.Stop(); } catch { }
            throw;
        }

        _listener = listener;
        Port = port;
        DataDirectory = dataDirectory;
        Stats.Reset();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsRunning = true;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(token), token);

        Status?.Invoke($"Listening on :{port} • data: {dataDirectory}", false);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;

        IsRunning = false;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }

        try
        {
            // ConfigureAwait(false) so the continuation never tries to resume on
            // the calling SynchronizationContext (deadlocks if caller blocks).
            if (_acceptLoop is not null)
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch { /* ignore */ }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
        Status?.Invoke("Stopped.", false);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Status?.Invoke($"Accept error: {ex.Message}", true);
                continue;
            }

            _ = Task.Run(() => HandleClientSafelyAsync(client, ct), ct);
        }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken outerCt)
    {
        using (client)
        using (var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt))
        {
            perCallCts.CancelAfter(TimeSpan.FromMinutes(60)); // per-connection cap (large files)
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = RequestTimeoutMs;
                client.SendTimeout    = RequestTimeoutMs;

                using var stream = client.GetStream();
                stream.ReadTimeout  = RequestTimeoutMs;
                stream.WriteTimeout = RequestTimeoutMs;

                await HandleRequestAsync(stream, perCallCts.Token);
            }
            catch (Exception ex) when (IsClientDisconnect(ex))
            {
                // PS4 dropped the connection mid-flight — normal during parallel range requests.
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                Stats.IncError();
                Status?.Invoke($"Connection error: {ex.Message}", true);
            }
        }
    }

    private async Task HandleRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        Stats.IncTotal();

        HttpHeadReader.Result head;
        try
        {
            head = await HttpHeadReader.ReadAsync(stream, ct);
        }
        catch (Exception ex)
        {
            Stats.IncError();
            try { await RangeFileServer.WriteSimpleResponseAsync(stream, 400, "Bad Request", "Could not parse request.\n", ct); } catch { }
            Log(RequestKind.Error, "?", "(unreadable)", 400, 0, ex.Message);
            return;
        }

        var parts = head.RequestLine.Split(' ');
        if (parts.Length < 2)
        {
            Stats.IncError();
            await RangeFileServer.WriteSimpleResponseAsync(stream, 400, "Bad Request", "Malformed request line.\n", ct);
            Log(RequestKind.Error, "?", head.RequestLine, 400, 0, "Malformed request line");
            return;
        }

        var method = parts[0].ToUpperInvariant();
        var requestTarget = parts[1];

        // HTTPS tunnel — PS4 uses these for PSN auth, image APIs, etc.
        if (method == "CONNECT")
        {
            await HandleConnectAsync(stream, requestTarget, ct);
            return;
        }

        // PS regex match against the absolute URL.
        if (PsRegex.TryMatch(requestTarget, out var fileName))
        {
            await ServePsFileAsync(stream, method, requestTarget, fileName, head, ct);
            return;
        }

        await ProxyUpstreamAsync(stream, method, requestTarget, head, ct);
    }

    private async Task HandleConnectAsync(NetworkStream clientStream, string target, CancellationToken ct)
    {
        Stats.IncProxy();

        // CONNECT target is "host:port" (no scheme).
        var colonIdx = target.LastIndexOf(':');
        if (colonIdx <= 0 || !int.TryParse(target.AsSpan(colonIdx + 1), out var targetPort))
        {
            try
            {
                await RangeFileServer.WriteSimpleResponseAsync(
                    clientStream, 400, "Bad Request", "Invalid CONNECT target.\n", ct);
            }
            catch { }
            Stats.IncError();
            Log(RequestKind.Error, "CONNECT", target, 400, 0, "Invalid CONNECT target");
            return;
        }

        var targetHost = target.Substring(0, colonIdx);

        using var upstream = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));
            await upstream.ConnectAsync(targetHost, targetPort, connectCts.Token);
        }
        catch (Exception ex)
        {
            try
            {
                await RangeFileServer.WriteSimpleResponseAsync(
                    clientStream, 502, "Bad Gateway",
                    $"Could not connect to {target}: {ex.Message}\n", ct);
            }
            catch { }
            Stats.IncError();
            Log(RequestKind.Error, "CONNECT", target, 502, 0, ex.Message);
            return;
        }

        // Tell the client the tunnel is open.
        var ok = "HTTP/1.1 200 Connection Established\r\n\r\n";
        try
        {
            await clientStream.WriteAsync(Encoding.ASCII.GetBytes(ok), ct);
            await clientStream.FlushAsync(ct);
        }
        catch
        {
            return;
        }

        var upstreamStream = upstream.GetStream();

        // Bidirectional byte relay until either side closes.
        long bytesUp = 0, bytesDown = 0;
        var t1 = RelayAsync(clientStream, upstreamStream, b => bytesUp += b, ct);
        var t2 = RelayAsync(upstreamStream, clientStream, b => bytesDown += b, ct);

        try { await Task.WhenAny(t1, t2); }
        catch { /* one side closed */ }

        // Close upstream so the still-running direction unblocks.
        try { upstream.Close(); } catch { }

        var total = bytesUp + bytesDown;
        Stats.AddBytes(total);
        Log(RequestKind.Proxy, "CONNECT", target, 200, total, "Tunnel closed");
    }

    private static async Task RelayAsync(
        Stream from, Stream to, Action<long> onBytes, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await from.ReadAsync(buffer.AsMemory(), ct);
                if (n == 0) break;
                await to.WriteAsync(buffer.AsMemory(0, n), ct);
                onBytes(n);
            }
        }
        catch
        {
            // Normal: the other side closed, or PS4 cancelled.
        }
    }

    private async Task ServePsFileAsync(
        NetworkStream stream,
        string method,
        string fullUrl,
        string fileName,
        HttpHeadReader.Result head,
        CancellationToken ct)
    {
        var safeName = Path.GetFileName(fileName); // strip any path separators
        var filePath = Path.Combine(DataDirectory, safeName);

        if (!File.Exists(filePath))
        {
            if (AllowSonyFallback)
            {
                // Forward to Sony — PS4 downloads through us instead of failing.
                Log(RequestKind.Fallback, method, fullUrl, 0, 0,
                    $"Forwarding to Sony: {safeName}", fileName: safeName);
                await ProxyUpstreamAsync(stream, method, fullUrl, head, ct, isFallback: true);
                return;
            }

            Stats.IncMiss();
            try
            {
                await RangeFileServer.WriteSimpleResponseAsync(
                    stream, 404, "Not Found",
                    $"File not found: {safeName}\nDownload it and place it in:\n{DataDirectory}\n" +
                    "Or enable 'Download missing files from Sony' in Settings.\n",
                    ct);
            }
            catch { /* client may have given up */ }

            Log(RequestKind.CacheMiss, method, fullUrl, 404, 0, safeName, fileName: safeName);
            return;
        }

        try
        {
            bool isHead = method == "HEAD";
            var sent = await RangeFileServer.SendAsync(stream, filePath, head.Headers, isHead, ct);

            Stats.IncHit();
            Stats.AddBytes(sent);
            Log(RequestKind.CacheHit, method, fullUrl, 200, sent, safeName, fileName: safeName);
        }
        catch (Exception ex) when (IsClientDisconnect(ex))
        {
            // PS4 cancelled while we were sending — no log, no error count.
        }
        catch (Exception ex)
        {
            Stats.IncError();
            Log(RequestKind.Error, method, fullUrl, 500, 0, ex.Message, fileName: safeName);
        }
    }

    private async Task ProxyUpstreamAsync(
        NetworkStream stream,
        string method,
        string requestTarget,
        HttpHeadReader.Result head,
        CancellationToken ct,
        bool isFallback = false)
    {
        if (!isFallback) Stats.IncProxy();

        if (!Uri.TryCreate(requestTarget, UriKind.Absolute, out var targetUri))
        {
            await RangeFileServer.WriteSimpleResponseAsync(stream, 400, "Bad Request",
                "Forward proxy expects absolute URLs in the request line.\n", ct);
            Log(RequestKind.Error, method, requestTarget, 400, 0, "Non-absolute URI");
            return;
        }

        try
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), targetUri);

            // Forward headers (skip hop-by-hop and Host — HttpClient sets Host from URI).
            byte[]? bodyBytes = null;
            if (head.Headers.TryGetValue("Content-Length", out var clStr) &&
                long.TryParse(clStr, out var contentLength) && contentLength > 0)
            {
                bodyBytes = await ReadBodyAsync(stream, head.BodyOverflow, contentLength, ct);
                req.Content = new ByteArrayContent(bodyBytes);
            }

            foreach (var (name, value) in head.Headers)
            {
                if (HopByHop.Contains(name)) continue;
                if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;

                if (req.Content is not null && IsContentHeader(name))
                {
                    req.Content.Headers.TryAddWithoutValidation(name, value);
                }
                else
                {
                    req.Headers.TryAddWithoutValidation(name, value);
                }
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(RequestTimeoutMs));

            using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            // Build response head.
            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase}\r\n");

            foreach (var h in response.Headers)
            {
                if (HopByHop.Contains(h.Key)) continue;
                foreach (var v in h.Value) sb.Append($"{h.Key}: {v}\r\n");
            }
            foreach (var h in response.Content.Headers)
            {
                if (HopByHop.Contains(h.Key)) continue;
                foreach (var v in h.Value) sb.Append($"{h.Key}: {v}\r\n");
            }
            sb.Append("Connection: close\r\n");
            sb.Append("\r\n");

            await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);

            long copied = 0;
            await using (var upstream = await response.Content.ReadAsStreamAsync(ct))
            {
                var buf = new byte[81920];
                while (true)
                {
                    int n = await upstream.ReadAsync(buf, ct);
                    if (n == 0) break;
                    await stream.WriteAsync(buf.AsMemory(0, n), ct);
                    copied += n;
                }
            }

            Stats.AddBytes(copied);
            Log(isFallback ? RequestKind.Fallback : RequestKind.Proxy,
                method, requestTarget, (int)response.StatusCode, copied,
                $"{(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception ex) when (IsClientDisconnect(ex))
        {
            // PS4 dropped the connection while we were forwarding — expected during
            // parallel range cancellations. Don't pollute the log.
        }
        catch (Exception ex)
        {
            Stats.IncError();
            try
            {
                await RangeFileServer.WriteSimpleResponseAsync(stream, 502, "Bad Gateway",
                    $"Upstream failure: {ex.Message}\n", ct);
            }
            catch { /* probably already wrote headers */ }

            Log(RequestKind.Error, method, requestTarget, 502, 0, ex.Message);
        }
    }

    /// <summary>
    /// True when the exception represents the *client* closing/aborting the
    /// connection mid-flight — a normal, expected event (PS4 cancels parallel
    /// range requests as it switches between download chunks). We don't want
    /// these in the error stats or the log.
    /// </summary>
    private static bool IsClientDisconnect(Exception ex)
    {
        if (ex is OperationCanceledException) return true;

        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is SocketException se &&
                (se.SocketErrorCode == SocketError.ConnectionAborted ||
                 se.SocketErrorCode == SocketError.ConnectionReset ||
                 se.SocketErrorCode == SocketError.OperationAborted ||
                 se.SocketErrorCode == SocketError.Shutdown))
            {
                return true;
            }
            if (cur is IOException && cur.Message.Contains("transport connection",
                                                          StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsContentHeader(string name)
    {
        // Headers that belong on HttpContent.Headers, not HttpRequestMessage.Headers.
        return name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Expires", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Last-Modified", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Allow", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadBodyAsync(
        NetworkStream stream, byte[] overflow, long contentLength, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        if (overflow.Length > 0)
        {
            var take = (int)Math.Min(overflow.Length, contentLength);
            ms.Write(overflow, 0, take);
        }
        long remaining = contentLength - ms.Length;

        var buf = new byte[8192];
        while (remaining > 0 && !ct.IsCancellationRequested)
        {
            int toRead = (int)Math.Min(buf.Length, remaining);
            int n = await stream.ReadAsync(buf.AsMemory(0, toRead), ct);
            if (n == 0) break;
            ms.Write(buf, 0, n);
            remaining -= n;
        }
        return ms.ToArray();
    }

    private void Log(
        RequestKind kind, string method, string url, int status, long bytes, string detail,
        string fileName = "")
    {
        var entry = new RequestLogEntry
        {
            Kind = kind,
            Method = method,
            Url = url,
            FileName = fileName,
            StatusCode = status,
            Bytes = bytes,
            Detail = detail
        };
        try { RequestLogged?.Invoke(entry); } catch { /* ignore subscriber errors */ }
    }

    public void Dispose()
    {
        // Run StopAsync on a thread-pool thread so its continuations never need
        // the caller's SynchronizationContext. Calling .Wait() here is safe
        // because nothing on this thread is awaited inside StopAsync.
        try
        {
            Task.Run(() => StopAsync()).Wait(TimeSpan.FromSeconds(5));
        }
        catch { /* ignore */ }
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
