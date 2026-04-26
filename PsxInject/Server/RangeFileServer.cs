using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace PsxInject.Server;

/// <summary>
/// Serves a file over a raw network stream with HTTP/1.1 Range support.
/// Critical for PS4 resumable downloads of large .pkg files.
/// </summary>
public static partial class RangeFileServer
{
    [GeneratedRegex(@"^bytes=(\d*)-(\d*)$", RegexOptions.IgnoreCase)]
    private static partial Regex RangeRegex();

    public static async Task<long> SendAsync(
        NetworkStream stream,
        string filePath,
        IReadOnlyDictionary<string, string> requestHeaders,
        bool isHeadRequest,
        CancellationToken ct)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            await WriteSimpleResponseAsync(stream, 404, "Not Found", "File not found.\n", ct);
            return 0;
        }

        long total = info.Length;
        long start = 0;
        long end = total - 1;
        bool partial = false;
        bool unsatisfiable = false;

        if (requestHeaders.TryGetValue("Range", out var rangeValue) && !string.IsNullOrWhiteSpace(rangeValue))
        {
            var m = RangeRegex().Match(rangeValue.Trim());
            if (m.Success)
            {
                var startStr = m.Groups[1].Value;
                var endStr   = m.Groups[2].Value;

                if (startStr.Length == 0 && endStr.Length > 0)
                {
                    // Suffix range: last N bytes.
                    if (long.TryParse(endStr, out var suffix) && suffix > 0)
                    {
                        if (suffix > total) suffix = total;
                        start = total - suffix;
                        end   = total - 1;
                        partial = true;
                    }
                    else unsatisfiable = true;
                }
                else if (startStr.Length > 0)
                {
                    if (!long.TryParse(startStr, out start)) unsatisfiable = true;
                    if (endStr.Length > 0 && !long.TryParse(endStr, out end)) unsatisfiable = true;

                    if (!unsatisfiable)
                    {
                        if (end >= total) end = total - 1;
                        if (start > end || start >= total) unsatisfiable = true;
                        else partial = true;
                    }
                }
            }
        }

        if (unsatisfiable)
        {
            var msg = $"Requested range not satisfiable. File length is {total}.";
            var bytes = Encoding.ASCII.GetBytes(msg);
            var head = new StringBuilder();
            head.Append("HTTP/1.1 416 Range Not Satisfiable\r\n");
            head.Append("Content-Type: text/plain\r\n");
            head.Append($"Content-Range: bytes */{total}\r\n");
            head.Append($"Content-Length: {bytes.Length}\r\n");
            head.Append("Connection: close\r\n\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
            await stream.WriteAsync(bytes, ct);
            return 0;
        }

        long contentLength = end - start + 1;

        var sb = new StringBuilder();
        sb.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        sb.Append("Content-Type: application/octet-stream\r\n");
        sb.Append($"Content-Length: {contentLength}\r\n");
        sb.Append("Accept-Ranges: bytes\r\n");
        if (partial) sb.Append($"Content-Range: bytes {start}-{end}/{total}\r\n");
        sb.Append($"Last-Modified: {info.LastWriteTimeUtc:R}\r\n");
        sb.Append($"ETag: \"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);

        if (isHeadRequest) return 0;

        long sent = 0;
        await using var fs = new FileStream(
            filePath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                BufferSize = 0,
                Options = FileOptions.SequentialScan | FileOptions.Asynchronous
            });

        if (start > 0) fs.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[81920];
        long remaining = contentLength;
        while (remaining > 0 && !ct.IsCancellationRequested)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int n = await fs.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (n == 0) break;
            await stream.WriteAsync(buffer.AsMemory(0, n), ct);
            remaining -= n;
            sent += n;
        }

        return sent;
    }

    public static async Task WriteSimpleResponseAsync(
        NetworkStream stream, int status, string reason, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder();
        head.Append($"HTTP/1.1 {status} {reason}\r\n");
        head.Append("Content-Type: text/plain; charset=utf-8\r\n");
        head.Append($"Content-Length: {bytes.Length}\r\n");
        head.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        await stream.WriteAsync(bytes, ct);
    }
}
