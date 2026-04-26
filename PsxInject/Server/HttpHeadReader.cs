using System.IO;
using System.Net.Sockets;
using System.Text;

namespace PsxInject.Server;

/// <summary>
/// Reads the request line and headers from a network stream and returns any extra bytes
/// the read overshot into (which belong to the request body).
/// </summary>
public static class HttpHeadReader
{
    private const int MaxHeaderBytes = 64 * 1024;

    public readonly struct Result
    {
        public Result(string requestLine, Dictionary<string, string> headers, byte[] bodyOverflow)
        {
            RequestLine = requestLine;
            Headers = headers;
            BodyOverflow = bodyOverflow;
        }
        public string RequestLine { get; }
        public Dictionary<string, string> Headers { get; }
        public byte[] BodyOverflow { get; }
    }

    public static async Task<Result> ReadAsync(NetworkStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int separatorEnd = -1;

        while (true)
        {
            int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0)
                throw new IOException("Client closed connection before completing headers.");

            ms.Write(buffer, 0, n);
            if (ms.Length > MaxHeaderBytes)
                throw new IOException("Request headers exceed maximum size.");

            var arr = ms.GetBuffer();
            var len = (int)ms.Length;
            for (int i = 3; i < len; i++)
            {
                if (arr[i - 3] == (byte)'\r' && arr[i - 2] == (byte)'\n' &&
                    arr[i - 1] == (byte)'\r' && arr[i]     == (byte)'\n')
                {
                    separatorEnd = i;
                    break;
                }
            }
            if (separatorEnd >= 0) break;
        }

        var raw = ms.GetBuffer();
        var totalLen = (int)ms.Length;
        var headerSection = Encoding.ASCII.GetString(raw, 0, separatorEnd - 3);

        var lines = headerSection.Split("\r\n");
        var requestLine = lines.Length > 0 ? lines[0] : "";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line)) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim();
            if (headers.TryGetValue(name, out var existing))
                headers[name] = existing + ", " + value;
            else
                headers[name] = value;
        }

        int overflowStart = separatorEnd + 1;
        int overflowLen = totalLen - overflowStart;
        var overflow = overflowLen > 0
            ? raw.AsSpan(overflowStart, overflowLen).ToArray()
            : Array.Empty<byte>();

        return new Result(requestLine, headers, overflow);
    }
}
