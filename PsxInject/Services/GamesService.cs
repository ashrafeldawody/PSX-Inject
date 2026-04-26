using System.Text.RegularExpressions;
using PsxInject.Models;

namespace PsxInject.Services;

public static partial class GamesService
{
    // Title ID format used on PS4: 4 letters + 5 digits (CUSA00182, PCAS, PLAS etc).
    [GeneratedRegex(@"(?<id>[A-Z]{4}\d{5})", RegexOptions.IgnoreCase)]
    private static partial Regex TitleIdRegex();

    [GeneratedRegex(@"(PATCH|UPDATE)", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateMarkerRegex();

    public static IReadOnlyList<Game> GroupFiles(
        IEnumerable<CachedFile> cachedFiles,
        IReadOnlyDictionary<string, string>? seenPkgs = null,
        IReadOnlyDictionary<string, string>? displayNames = null,
        IReadOnlyDictionary<string, long>? seenPkgSizes = null,
        IReadOnlyDictionary<string, GameMetadata>? metadata = null)
    {
        var byTitle = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
        var cachedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in cachedFiles)
        {
            cachedNames.Add(file.Name);
            AddFile(byTitle, file, displayNames, metadata);
        }

        if (seenPkgs is not null)
        {
            foreach (var (name, url) in seenPkgs)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (cachedNames.Contains(name)) continue;

                long size = 0;
                seenPkgSizes?.TryGetValue(name, out size);

                var stub = new CachedFile
                {
                    Name = name,
                    FullPath = "",
                    Size = size,
                    LastModified = DateTime.MinValue,
                    IsCached = false,
                    SourceUrl = url ?? ""
                };
                AddFile(byTitle, stub, displayNames, metadata);
            }
        }

        // Sort each game's files by part number, then alphabetic.
        foreach (var g in byTitle.Values)
        {
            g.GameFiles.Sort(ComparePart);
            g.Updates.Sort(ComparePart);
        }

        return byTitle.Values
            .OrderByDescending(g => g.TotalSize)
            .ThenBy(g => g.EffectiveName)
            .ToList();
    }

    private static int ComparePart(CachedFile a, CachedFile b)
    {
        var na = ExtractPartNumber(a.Name);
        var nb = ExtractPartNumber(b.Name);
        if (na.HasValue && nb.HasValue && na != nb) return na.Value.CompareTo(nb.Value);
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"_(\d+)\.pkg$", RegexOptions.IgnoreCase)]
    private static partial Regex PartNumberRegex();

    public static int? ExtractPartNumber(string filename)
    {
        var m = PartNumberRegex().Match(filename);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    /// <summary>
    /// Replaces the trailing _N.pkg in the URL's path (not its query string)
    /// with _newN.pkg. Returns null only if the URL has no _N.pkg suffix at all
    /// (so we can't substitute meaningfully). Returns the original URL when the
    /// substitution is a no-op (newN equals current N) — important for n=0.
    /// </summary>
    public static string? SubstitutePartNumber(string absoluteUrl, int newN)
    {
        if (!Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var uri)) return null;
        if (!PartNumberRegex().IsMatch(uri.AbsolutePath)) return null;
        var newPath = PartNumberRegex().Replace(uri.AbsolutePath, $"_{newN}.pkg");
        return $"{uri.Scheme}://{uri.Authority}{newPath}{uri.Query}";
    }

    /// <summary>
    /// Strips everything after `.pkg` (query params, fragments) so the link is
    /// clean enough to paste into a download manager without session-bound tokens.
    /// </summary>
    public static string StripPkgQuery(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var idx = url.IndexOf(".pkg", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return url;
        int after = idx + 4;
        if (after >= url.Length) return url;
        if (url[after] == '?' || url[after] == '#') return url.Substring(0, after);
        return url;
    }

    public static string SubstitutePartFilename(string filename, int newN) =>
        PartNumberRegex().Replace(filename, $"_{newN}.pkg");

    private static void AddFile(
        Dictionary<string, Game> byTitle,
        CachedFile file,
        IReadOnlyDictionary<string, string>? displayNames,
        IReadOnlyDictionary<string, GameMetadata>? metadata = null)
    {
        var titleId = ExtractTitleId(file.Name);
        if (string.IsNullOrEmpty(titleId)) titleId = "Unknown";
        else titleId = titleId.ToUpperInvariant();

        if (!byTitle.TryGetValue(titleId, out var game))
        {
            string userOverride = "";
            displayNames?.TryGetValue(titleId, out userOverride!);

            game = new Game
            {
                TitleId = titleId,
                DisplayName = userOverride ?? "",
                DerivedName = DeriveDisplayName(file.Name, titleId),
                Metadata = metadata is not null && metadata.TryGetValue(titleId, out var m) ? m : null
            };
            byTitle[titleId] = game;
        }

        if (IsUpdateFile(file.Name))
            game.Updates.Add(file);
        else
            game.GameFiles.Add(file);
    }

    public static string ExtractTitleId(string fileName)
    {
        var m = TitleIdRegex().Match(fileName);
        return m.Success ? m.Groups["id"].Value : "";
    }

    public static bool IsUpdateFile(string fileName) => UpdateMarkerRegex().IsMatch(fileName);

    private static string DeriveDisplayName(string fileName, string titleId)
    {
        // Strip extension, region prefix, title id, content suffix.
        // Example: EP4432-CUSA00182_00-WARTHUNDER000000_0.pkg → "Warthunder"
        var name = System.IO.Path.GetFileNameWithoutExtension(fileName);

        // Remove the title id and surrounding _00-/-_00 markers.
        name = Regex.Replace(name, @"^[A-Z]{2}\d{3,4}-", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, $@"{Regex.Escape(titleId)}_\d+-?", "", RegexOptions.IgnoreCase);

        // Strip part-index suffix.
        name = Regex.Replace(name, @"_\d+$", "", RegexOptions.IgnoreCase);

        // Strip trailing zero/X padding (e.g. WARTHUNDER000000 → WARTHUNDER).
        name = Regex.Replace(name, @"[0X]+$", "", RegexOptions.IgnoreCase);

        // Remove patch/update tokens.
        name = Regex.Replace(name, @"\b(PATCH|UPDATE|V\d+)\b", "", RegexOptions.IgnoreCase);

        name = name.Trim().Trim('-', '_').Trim();
        if (string.IsNullOrWhiteSpace(name)) return titleId;

        // Title-case the result so WARTHUNDER → Warthunder.
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo
            .ToTitleCase(name.ToLowerInvariant());
    }
}
