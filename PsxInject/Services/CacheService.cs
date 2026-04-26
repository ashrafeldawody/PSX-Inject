using System.IO;
using PsxInject.Models;

namespace PsxInject.Services;

public static class CacheService
{
    public static IReadOnlyList<CachedFile> EnumerateFiles(string dataDir)
    {
        try
        {
            if (!Directory.Exists(dataDir)) return Array.Empty<CachedFile>();

            return new DirectoryInfo(dataDir)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f => !f.Name.StartsWith(".", StringComparison.Ordinal))
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => new CachedFile
                {
                    Name = f.Name,
                    FullPath = f.FullName,
                    Size = f.Length,
                    LastModified = f.LastWriteTime
                })
                .ToList();
        }
        catch
        {
            return Array.Empty<CachedFile>();
        }
    }

    public static long GetTotalSize(IEnumerable<CachedFile> files) => files.Sum(f => f.Size);

    public static void EnsureExists(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch { /* ignore */ }
    }
}
