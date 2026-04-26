namespace PsxInject.Models;

/// <summary>
/// Game metadata parsed from PSN's TMDB JSON
/// (http://tmdb.np.dl.playstation.net/tmdb2/&lt;TitleId&gt;_&lt;hash&gt;/&lt;TitleId&gt;.json).
/// Persisted to settings.json so the Games tab stays informative across launches.
/// </summary>
public class GameMetadata
{
    public string TitleId { get; set; } = "";       // e.g. "CUSA00967" (no _00 suffix)
    public string NpTitleId { get; set; } = "";     // e.g. "CUSA00967_00" (raw from JSON)
    public string Name { get; set; } = "";
    public string ContentId { get; set; } = "";
    public string Category { get; set; } = "";       // "gd" = game disc, etc.
    public string IconUrl { get; set; } = "";
    public string BackgroundUrl { get; set; } = "";
    public int Revision { get; set; }
    public int PatchRevision { get; set; }

    public string CategoryDisplay => Category?.ToUpperInvariant() switch
    {
        "GD" => "Game",
        "GP" => "Patch",
        "AC" => "Add-on",
        "AL" => "Lite",
        _ => Category ?? ""
    };
}
