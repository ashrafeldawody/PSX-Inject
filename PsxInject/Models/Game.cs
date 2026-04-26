using PsxInject.Server;

namespace PsxInject.Models;

public class Game
{
    public string TitleId { get; init; } = "";
    public string DisplayName { get; set; } = "";
    public List<CachedFile> GameFiles { get; init; } = new();
    public List<CachedFile> Updates { get; init; } = new();
    public GameMetadata? Metadata { get; init; }

    /// <summary>Filename-derived fallback name; used only when no user override and no TMDB.</summary>
    public string DerivedName { get; init; } = "";

    public long TotalSize => GameFiles.Sum(f => f.Size) + Updates.Sum(f => f.Size);
    public long GameFilesSize => GameFiles.Sum(f => f.Size);
    public long UpdatesSize => Updates.Sum(f => f.Size);
    public int FileCount => GameFiles.Count + Updates.Count;

    public string TotalSizeText => FormatHelpers.FormatBytes(TotalSize);
    public string GameFilesSizeText => FormatHelpers.FormatBytes(GameFilesSize);
    public string UpdatesSizeText => FormatHelpers.FormatBytes(UpdatesSize);

    public string EffectiveName =>
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName :
        Metadata is { Name.Length: > 0 } ? Metadata.Name :
        !string.IsNullOrWhiteSpace(DerivedName) ? DerivedName :
        TitleId;

    public string SubtitleText => $"{TitleId} • {FileCount} files • {TotalSizeText}";

    public bool HasUpdates => Updates.Count > 0;
    public bool HasGameFiles => GameFiles.Count > 0;

    public bool HasIcon => Metadata is { IconUrl.Length: > 0 };
    public bool HasBackground => Metadata is { BackgroundUrl.Length: > 0 };
    public string IconUrl => Metadata?.IconUrl ?? "";
    public string BackgroundUrl => Metadata?.BackgroundUrl ?? "";
    public string ContentId => Metadata?.ContentId ?? "";
    public string CategoryText => Metadata?.CategoryDisplay ?? "";
    public bool HasContentId => !string.IsNullOrEmpty(ContentId);
    public bool HasCategory => !string.IsNullOrEmpty(CategoryText);
}
