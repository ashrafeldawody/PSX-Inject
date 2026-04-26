using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsxInject.Models;

public enum CloseAction
{
    Ask,
    RunInBackground,
    Exit
}

public class AppSettings
{
    public int Port { get; set; } = 8080;
    public string DataDirectory { get; set; } = "";
    public CloseAction CloseAction { get; set; } = CloseAction.Ask;
    public bool AutoStartServer { get; set; } = false;
    public int RequestTimeoutMs { get; set; } = 30_000;
    public int MaxLogEntries { get; set; } = 500;

    /// <summary>
    /// Bumped when defaults change so existing settings.json files can be migrated
    /// without trapping users on stale values.
    /// </summary>
    public int SettingsVersion { get; set; } = 0;

    /// <summary>
    /// When a .pkg request matches the PSN URL pattern but the file is not in the
    /// data folder, this controls what happens:
    ///   false → return 404 (PS4 download fails until user manually places the file)
    ///   true  → forward the request to Sony's servers so the PS4 downloads through us
    /// Defaults to false to mirror the original index.js behavior.
    /// </summary>
    public bool AllowSonyFallback { get; set; } = false;

    /// <summary>
    /// User-editable friendly names keyed by Title ID (e.g. "CUSA00182" → "War Thunder").
    /// </summary>
    public Dictionary<string, string> GameDisplayNames { get; set; } = new();

    /// <summary>
    /// Pkg filenames the PS4 has requested (or that we've discovered by probing
    /// adjacent part numbers), mapped to a recent absolute URL so we can copy
    /// download links for them. Keyed by filename to dedupe across sessions.
    /// </summary>
    public Dictionary<string, string> SeenPkgs { get; set; } = new();

    /// <summary>filename → byte size, from HEAD probe Content-Length.</summary>
    public Dictionary<string, long> SeenPkgSizes { get; set; } = new();

    /// <summary>Title ID (without the _NN suffix) → parsed PSN TMDB metadata.</summary>
    public Dictionary<string, GameMetadata> GameMetadataMap { get; set; } = new();

    [JsonIgnore]
    public string EffectiveDataDirectory =>
        string.IsNullOrWhiteSpace(DataDirectory) ? DefaultDataDirectory : DataDirectory;

    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PsxInject", "data");
}
