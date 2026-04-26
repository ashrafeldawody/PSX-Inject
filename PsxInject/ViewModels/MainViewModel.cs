using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsxInject.Models;
using PsxInject.Server;
using PsxInject.Services;

namespace PsxInject.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ProxyServer _server = new();
    private readonly DispatcherTimer _tickTimer;
    private readonly Dispatcher _dispatcher;
    private AppSettings _settings;

    public ObservableCollection<RequestLogEntry> Requests { get; } = new();
    public ObservableCollection<CachedFile> CachedFiles { get; } = new();
    public ObservableCollection<string> LocalAddresses { get; } = new();
    public ObservableCollection<Game> Games { get; } = new();

    /// <summary>filename → most recent absolute URL we've seen for that pkg part.</summary>
    private readonly Dictionary<string, string> _seenPkgs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>filename → byte size from HEAD probe.</summary>
    private readonly Dictionary<string, long> _seenPkgSizes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Title ID → TMDB metadata.</summary>
    private readonly Dictionary<string, GameMetadata> _gameMetadata = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _tmdbFetched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pattern keys we've already kicked off discovery for, to avoid re-probing.</summary>
    private readonly HashSet<string> _discoveryStarted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Shared HttpClient for probes — separate from the proxy's client.</summary>
    private static readonly HttpClient _probeHttp = CreateProbeClient();

    private static HttpClient CreateProbeClient()
    {
        var http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseProxy = false
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        // Some PSN edge nodes reject requests without a UA. Use a generic one.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (PsxInject)");
        http.DefaultRequestHeaders.AcceptEncoding.Clear();
        return http;
    }

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "Stopped";
    [ObservableProperty] private bool _statusIsError;

    // Settings (bindable copies; pushed back to AppSettings on save)
    [ObservableProperty] private int _port;
    [ObservableProperty] private string _dataDirectory = "";
    [ObservableProperty] private bool _autoStartServer;
    [ObservableProperty] private CloseAction _closeAction;
    [ObservableProperty] private bool _allowSonyFallback;

    // Stats (refreshed by tick timer)
    [ObservableProperty] private long _totalRequests;
    [ObservableProperty] private long _cacheHits;
    [ObservableProperty] private long _cacheMisses;
    [ObservableProperty] private long _proxyRequests;
    [ObservableProperty] private long _errors;
    [ObservableProperty] private string _bytesServedText = "0 B";
    [ObservableProperty] private string _uptimeText = "0s";
    [ObservableProperty] private string _cachedTotalText = "0 B";
    [ObservableProperty] private int _cachedFileCount;

    // Port-conflict banner
    [ObservableProperty] private bool _isPortConflictBannerVisible;
    [ObservableProperty] private int _suggestedPort;
    [ObservableProperty] private int _conflictingPort;

    // Firewall banner
    [ObservableProperty] private bool _isFirewallBannerVisible;
    [ObservableProperty] private bool _isFirewallBusy;
    [ObservableProperty] private string _firewallBannerText =
        "Windows Firewall might block your PS4 from reaching this proxy.";
    [ObservableProperty] private string _firewallErrorDetail = "";
    [ObservableProperty] private string _firewallManualCommand = "";
    [ObservableProperty] private bool _isFirewallErrorVisible;

    // Selected nav tab
    [ObservableProperty] private int _selectedTab;

    public string ProxyHint =>
        LocalAddresses.Count > 0
            ? $"Set PS proxy to:  {LocalAddresses[0]} : {Port}"
            : $"Set PS proxy to:  <your PC IP> : {Port}";

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _settings = SettingsService.Load();

        _port = _settings.Port;
        _dataDirectory = _settings.EffectiveDataDirectory;
        _autoStartServer = _settings.AutoStartServer;
        _closeAction = _settings.CloseAction;
        _allowSonyFallback = _settings.AllowSonyFallback;

        // Restore previously-seen pkg filenames so the Games tab survives restarts.
        foreach (var (name, url) in _settings.SeenPkgs)
            if (!string.IsNullOrWhiteSpace(name))
                _seenPkgs[name] = url ?? "";

        foreach (var (name, size) in _settings.SeenPkgSizes)
            if (!string.IsNullOrWhiteSpace(name))
                _seenPkgSizes[name] = size;

        foreach (var (title, meta) in _settings.GameMetadataMap)
            if (!string.IsNullOrWhiteSpace(title) && meta is not null)
                _gameMetadata[title] = meta;

        _server.AllowSonyFallback = _allowSonyFallback;
        _server.RequestLogged += OnRequestLogged;
        _server.Status += OnServerStatus;

        _tickTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _tickTimer.Tick += (_, _) => UpdateStats();
        _tickTimer.Start();

        RefreshLocalAddresses();
        RefreshCachedFiles();
    }

    public async Task InitializeAsync()
    {
        if (_settings.AutoStartServer)
        {
            await Start();
        }
        else
        {
            // Even when not auto-starting, surface the firewall banner up front.
            _ = CheckFirewallAsync();
        }
    }

    private void OnRequestLogged(RequestLogEntry entry)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnRequestLogged(entry));
            return;
        }

        Requests.Insert(0, entry);
        while (Requests.Count > _settings.MaxLogEntries)
            Requests.RemoveAt(Requests.Count - 1);

        // Track every pkg request — the seen list feeds the Games tab.
        if (!string.IsNullOrEmpty(entry.FileName) && !string.IsNullOrEmpty(entry.Url) &&
            entry.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            TrackSeenPkg(entry.FileName, entry.Url, size: null);

            // When we see part 0, probe _0..N until we hit 404. Probe 0 too so we
            // get its size via Content-Length.
            if (GamesService.ExtractPartNumber(entry.FileName) == 0)
            {
                _ = DiscoverAdditionalPartsAsync(entry.Url, entry.FileName);
            }
        }

        // PSN's TMDB endpoint contains rich game metadata (name, icon, background image).
        if (!string.IsNullOrEmpty(entry.Url) &&
            entry.Url.Contains("/tmdb2/", StringComparison.OrdinalIgnoreCase) &&
            entry.Url.Contains(".json", StringComparison.OrdinalIgnoreCase))
        {
            _ = FetchTmdbMetadataAsync(entry.Url);
        }

        if (entry.Kind == RequestKind.CacheHit || entry.Kind == RequestKind.CacheMiss)
            RefreshCachedFiles();
    }

    private void TrackSeenPkg(string filename, string url, long? size)
    {
        bool changed = false;

        if (!_seenPkgs.TryGetValue(filename, out var existing) || existing != url)
        {
            _seenPkgs[filename] = url;
            _settings.SeenPkgs[filename] = url;
            changed = true;
        }

        if (size is long s && s > 0 &&
            (!_seenPkgSizes.TryGetValue(filename, out var existingSize) || existingSize != s))
        {
            _seenPkgSizes[filename] = s;
            _settings.SeenPkgSizes[filename] = s;
            changed = true;
        }

        if (changed)
        {
            SettingsService.Save(_settings);
            RefreshCachedFiles();
        }
    }

    private async Task DiscoverAdditionalPartsAsync(string baseUrl, string baseFilename)
    {
        var patternKey = System.Text.RegularExpressions.Regex.Replace(
            baseFilename, @"_\d+\.pkg$", "_X.pkg");
        if (!_discoveryStarted.Add(patternKey)) return;

        SetStatus($"Discovering parts for {Path.GetFileNameWithoutExtension(baseFilename)}…", false);

        int found = 0;
        int errorStreak = 0;
        for (int n = 0; n < 100; n++)
        {
            var signedUrl = GamesService.SubstitutePartNumber(baseUrl, n);
            if (signedUrl is null) break;

            var probeFilename = GamesService.SubstitutePartFilename(baseFilename, n);

            // Try multiple variants — the first definitive result (Found OR 404) wins.
            // Each attempt is logged in the Requests tab so the user can see what
            // PSN actually returned.
            var attempts = BuildAttempts(signedUrl);

            ProbeResult? finalResult = null;
            foreach (var (label, url, range) in attempts)
            {
                var r = await TryProbeAsync(url, range, label).ConfigureAwait(false);
                if (r.Exists || r.IsNotFound)
                {
                    finalResult = r;
                    break;
                }
                finalResult = r; // keep last for diagnostic if all fail
            }

            var result = finalResult!.Value;

            if (result.IsNotFound)
            {
                SetStatus(found > 0
                    ? $"Discovered {found} parts."
                    : $"No parts found (last: {result.Diagnostic})", false);
                break;
            }

            if (!result.Exists)
            {
                // PSN signs URLs against existing paths. Past the last part
                // (e.g. _9 when only _0…_8 exist), fresh probes 4xx (typically
                // 403). Treat any 4xx after at least one success as the
                // natural end of the walk, not an error.
                bool isClientError = result.Diagnostic.StartsWith("HTTP 4",
                    StringComparison.Ordinal);

                if (found > 0 && isClientError)
                {
                    SetStatus($"Discovered {found} parts.", false);
                    break;
                }

                errorStreak++;
                SetStatus($"Probe of _{n} failed: {result.Diagnostic}", true);
                if (errorStreak >= 2) break;
                continue;
            }

            errorStreak = 0;
            found++;

            if (_dispatcher.CheckAccess())
                TrackSeenPkg(probeFilename, signedUrl, result.Size);
            else
                _dispatcher.Invoke(() => TrackSeenPkg(probeFilename, signedUrl, result.Size));

            SetStatus(
                $"Discovering: found {found} parts (latest _{n}, " +
                $"{Server.FormatHelpers.FormatBytes(result.Size ?? 0)})",
                false);
        }
    }

    private static IEnumerable<(string label, string url, bool useRange)> BuildAttempts(string signedUrl)
    {
        var bare = StripQuery(signedUrl);
        // Order matters: cheapest/most-likely first.
        yield return ("PROBE-bare",     bare,      false);
        yield return ("PROBE-bare+rng", bare,      true);
        if (bare != signedUrl)
        {
            yield return ("PROBE-signed",     signedUrl, false);
            yield return ("PROBE-signed+rng", signedUrl, true);
        }
    }

    private readonly struct ProbeResult
    {
        public bool Exists { get; init; }
        public bool IsNotFound { get; init; }
        public long? Size { get; init; }
        public string Diagnostic { get; init; }
    }

    private async Task<ProbeResult> TryProbeAsync(string url, bool useRange, string label)
    {
        int statusCode = 0;
        long? size = null;
        string detail;
        ProbeResult result;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (useRange)
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);

            using var resp = await _probeHttp.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

            statusCode = (int)resp.StatusCode;
            size = resp.Content.Headers.ContentRange?.Length
                   ?? resp.Content.Headers.ContentLength;

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                detail = "404 — end of parts";
                result = new ProbeResult { IsNotFound = true, Diagnostic = "404" };
            }
            else if (!resp.IsSuccessStatusCode)
            {
                detail = $"HTTP {statusCode} {resp.ReasonPhrase}";
                result = new ProbeResult { Diagnostic = detail };
            }
            else
            {
                detail = size.HasValue
                    ? $"{statusCode} OK • {Server.FormatHelpers.FormatBytes(size.Value)}"
                    : $"{statusCode} OK (no size)";
                result = new ProbeResult { Exists = true, Size = size, Diagnostic = detail };
            }
        }
        catch (Exception ex)
        {
            detail = $"network error: {ex.Message}";
            result = new ProbeResult { Diagnostic = detail };
        }

        // Surface this attempt in the Requests tab so users can see exactly
        // what PSN said.
        LogProbe(label, url, statusCode, size ?? 0, detail);
        return result;
    }

    private void LogProbe(string method, string url, int status, long bytes, string detail)
    {
        var entry = new RequestLogEntry
        {
            Kind = status == 404 ? RequestKind.CacheMiss
                  : status >= 400 ? RequestKind.Error
                  : RequestKind.Proxy,
            Method = method,
            Url = url,
            StatusCode = status,
            Bytes = bytes,
            Detail = detail
        };
        if (_dispatcher.CheckAccess()) AddToRequestsLog(entry);
        else _dispatcher.BeginInvoke(() => AddToRequestsLog(entry));
    }

    private void AddToRequestsLog(RequestLogEntry entry)
    {
        Requests.Insert(0, entry);
        while (Requests.Count > _settings.MaxLogEntries)
            Requests.RemoveAt(Requests.Count - 1);
    }

    private static string StripQuery(string url)
    {
        var idx = url.IndexOf('?');
        return idx < 0 ? url : url.Substring(0, idx);
    }

    private void SetStatus(string text, bool isError)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => SetStatus(text, isError));
            return;
        }
        StatusText = text;
        StatusIsError = isError;
    }

    private async Task FetchTmdbMetadataAsync(string tmdbUrl)
    {
        if (!_tmdbFetched.Add(tmdbUrl)) return;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, tmdbUrl);
            using var resp = await _probeHttp.SendAsync(
                req, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode) return;

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var meta = ParseTmdb(json);
            if (meta is null || string.IsNullOrEmpty(meta.TitleId)) return;

            if (_dispatcher.CheckAccess()) StoreMetadata(meta);
            else _dispatcher.Invoke(() => StoreMetadata(meta));
        }
        catch
        {
            // Best-effort — fall back to filename-derived display name.
        }
    }

    private void StoreMetadata(GameMetadata meta)
    {
        _gameMetadata[meta.TitleId] = meta;
        _settings.GameMetadataMap[meta.TitleId] = meta;
        SettingsService.Save(_settings);

        StatusText = $"Got metadata: {meta.Name}";
        StatusIsError = false;

        RefreshCachedFiles();
    }

    private static GameMetadata? ParseTmdb(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            string npTitle = root.TryGetProperty("npTitleId", out var p) ? p.GetString() ?? "" : "";
            // Trim "_NN" suffix to match our internal Title ID convention (e.g. CUSA00967).
            string title = System.Text.RegularExpressions.Regex.Replace(
                npTitle, @"_\d+$", "").ToUpperInvariant();
            if (string.IsNullOrEmpty(title)) return null;

            string name = "";
            if (root.TryGetProperty("names", out var names) && names.ValueKind == System.Text.Json.JsonValueKind.Array
                && names.GetArrayLength() > 0)
            {
                if (names[0].TryGetProperty("name", out var n)) name = n.GetString() ?? "";
            }

            string iconUrl = "";
            if (root.TryGetProperty("icons", out var icons) && icons.ValueKind == System.Text.Json.JsonValueKind.Array
                && icons.GetArrayLength() > 0)
            {
                if (icons[0].TryGetProperty("icon", out var i)) iconUrl = i.GetString() ?? "";
            }

            string bg = root.TryGetProperty("backgroundImage", out var b) ? b.GetString() ?? "" : "";
            string contentId = root.TryGetProperty("contentId", out var c) ? c.GetString() ?? "" : "";
            string category = root.TryGetProperty("category", out var cat) ? cat.GetString() ?? "" : "";
            int revision = root.TryGetProperty("revision", out var r) && r.TryGetInt32(out var rv) ? rv : 0;
            int patchRev = root.TryGetProperty("patchRevision", out var pr) && pr.TryGetInt32(out var prv) ? prv : 0;

            return new GameMetadata
            {
                TitleId = title,
                NpTitleId = npTitle,
                Name = name,
                IconUrl = iconUrl,
                BackgroundUrl = bg,
                ContentId = contentId,
                Category = category,
                Revision = revision,
                PatchRevision = patchRev
            };
        }
        catch
        {
            return null;
        }
    }

    [RelayCommand]
    private void CopyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // For .pkg URLs, strip session-bound query tokens so the copied link
        // is clean enough to paste straight into a download manager.
        var clean = GamesService.StripPkgQuery(text);

        try
        {
            Clipboard.SetText(clean);
            StatusText = clean != text
                ? "Copied (params stripped)."
                : "Copied to clipboard.";
            StatusIsError = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Copy failed: {ex.Message}";
            StatusIsError = true;
        }
    }

    private void OnServerStatus(string message, bool isError)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnServerStatus(message, isError));
            return;
        }

        StatusText = message;
        StatusIsError = isError;
    }

    private void UpdateStats()
    {
        var s = _server.Stats;
        TotalRequests = s.TotalRequests;
        CacheHits = s.CacheHits;
        CacheMisses = s.CacheMisses;
        ProxyRequests = s.ProxyRequests;
        Errors = s.Errors;
        BytesServedText = FormatHelpers.FormatBytes(s.BytesServed);
        UptimeText = _server.IsRunning ? FormatHelpers.FormatUptime(s.Uptime) : "—";
        IsRunning = _server.IsRunning;
    }

    [RelayCommand]
    private async Task Start()
    {
        if (_server.IsRunning) return;

        // Pre-check is advisory only — the authoritative answer comes from the
        // real bind below. The check is racy on its own but gives a faster banner
        // before we even try.
        if (!NetworkService.IsPortAvailable(Port))
        {
            ShowPortConflict(Port);
            return;
        }

        IsPortConflictBannerVisible = false;

        try
        {
            CacheService.EnsureExists(DataDirectory);
            _server.AllowSonyFallback = AllowSonyFallback;
            await _server.StartAsync(Port, DataDirectory);
            UpdateStats();
            RefreshCachedFiles();
            _ = CheckFirewallAsync();
        }
        catch (PortInUseException)
        {
            ShowPortConflict(Port);
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to start: {ex.Message}";
            StatusIsError = true;
        }
    }

    private void ShowPortConflict(int port)
    {
        ConflictingPort = port;
        SuggestedPort = NetworkService.FindAvailablePort(port);
        IsPortConflictBannerVisible = true;
        StatusText = $"Port {port} is in use.";
        StatusIsError = true;
        IsRunning = false;
    }

    [RelayCommand]
    private async Task Stop()
    {
        try
        {
            await _server.StopAsync();
        }
        finally
        {
            UpdateStats();
        }
    }

    [RelayCommand]
    private async Task Restart()
    {
        await Stop();
        await Start();
    }

    [RelayCommand]
    private async Task UseSuggestedPort()
    {
        Port = SuggestedPort;
        IsPortConflictBannerVisible = false;
        _settings.Port = Port;
        SettingsService.Save(_settings);
        await Start();
    }

    [RelayCommand]
    private void DismissPortConflict() => IsPortConflictBannerVisible = false;

    [RelayCommand]
    private void OpenSettingsFromBanner()
    {
        IsPortConflictBannerVisible = false;
        SelectedTab = 4; // Settings tab (after Games is added)
    }

    private async Task CheckFirewallAsync()
    {
        // Best-effort: a missing exe path (running from `dotnet run`) means we can't add a rule.
        if (string.IsNullOrEmpty(FirewallService.CurrentExecutablePath) ||
            !FirewallService.CurrentExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var status = await Task.Run(() => FirewallService.CheckRule());
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => ApplyFirewallStatus(status));
        }
        else ApplyFirewallStatus(status);
    }

    private void ApplyFirewallStatus(FirewallService.Status status)
    {
        IsFirewallBannerVisible = status == FirewallService.Status.Missing;
        FirewallBannerText = status switch
        {
            FirewallService.Status.Missing =>
                "Windows Firewall has no inbound rule for PSX inject. Your PS4 may not be able to reach this proxy.",
            _ => ""
        };
    }

    [RelayCommand]
    private async Task AllowFirewall()
    {
        IsFirewallBusy = true;
        IsFirewallErrorVisible = false;
        FirewallErrorDetail = "";
        try
        {
            var result = await Task.Run(FirewallService.TryAddRuleElevated);
            FirewallManualCommand = result.ManualCommand;

            if (result.Success)
            {
                IsFirewallBannerVisible = false;
                IsFirewallErrorVisible = false;
                StatusText = "Firewall rule added — PS4 can now reach this proxy.";
                StatusIsError = false;
            }
            else
            {
                IsFirewallErrorVisible = true;
                FirewallErrorDetail = result.Detail;
                StatusText = "Firewall rule was not added. See details in the banner.";
                StatusIsError = true;
            }
        }
        finally
        {
            IsFirewallBusy = false;
        }
    }

    [RelayCommand]
    private void CopyFirewallCommand()
    {
        if (string.IsNullOrWhiteSpace(FirewallManualCommand)) return;
        try
        {
            Clipboard.SetText(FirewallManualCommand);
            StatusText = "Command copied. Paste into an admin Command Prompt or PowerShell.";
            StatusIsError = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Copy failed: {ex.Message}";
            StatusIsError = true;
        }
    }

    [RelayCommand]
    private void DismissFirewall()
    {
        IsFirewallBannerVisible = false;
        IsFirewallErrorVisible = false;
    }

    [RelayCommand]
    private void ClearLog() => Requests.Clear();

    [RelayCommand]
    private void CopyUrl(RequestLogEntry? entry)
    {
        if (entry is null || string.IsNullOrEmpty(entry.Url)) return;
        try
        {
            Clipboard.SetText(entry.Url);
            StatusText = $"Copied URL for {entry.FileName}";
            StatusIsError = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Copy failed: {ex.Message}";
            StatusIsError = true;
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => CacheService.OpenInExplorer(DataDirectory);

    [RelayCommand]
    private void RefreshCachedFiles()
    {
        var files = CacheService.EnumerateFiles(DataDirectory);
        CachedFiles.Clear();
        foreach (var f in files) CachedFiles.Add(f);
        CachedFileCount = files.Count;
        CachedTotalText = FormatHelpers.FormatBytes(CacheService.GetTotalSize(files));

        // Re-group by game (cached + seen-but-not-cached) with sizes and metadata.
        Games.Clear();
        foreach (var g in GamesService.GroupFiles(
                     files, _seenPkgs, _settings.GameDisplayNames, _seenPkgSizes, _gameMetadata))
            Games.Add(g);
    }

    [RelayCommand]
    private void RefreshLocalAddresses()
    {
        LocalAddresses.Clear();
        foreach (var ip in NetworkService.GetLocalIPv4Addresses())
            LocalAddresses.Add(ip);
        OnPropertyChanged(nameof(ProxyHint));
    }

    [RelayCommand]
    private void BrowseDataFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose data folder",
            InitialDirectory = Directory.Exists(DataDirectory) ? DataDirectory : ""
        };
        if (dlg.ShowDialog() == true)
        {
            DataDirectory = dlg.FolderName;
        }
    }

    [RelayCommand]
    private void ResetDataFolder() => DataDirectory = AppSettings.DefaultDataDirectory;

    [RelayCommand]
    private async Task SaveSettings()
    {
        _settings.Port = Port;
        _settings.DataDirectory = DataDirectory;
        _settings.AutoStartServer = AutoStartServer;
        _settings.CloseAction = CloseAction;
        _settings.AllowSonyFallback = AllowSonyFallback;
        SettingsService.Save(_settings);

        _server.AllowSonyFallback = AllowSonyFallback;

        StatusText = "Settings saved.";
        StatusIsError = false;

        if (_server.IsRunning &&
            (_server.Port != Port ||
             !string.Equals(_server.DataDirectory, DataDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            await Restart();
        }
        else
        {
            RefreshCachedFiles();
        }

        OnPropertyChanged(nameof(ProxyHint));
    }

    public void PersistCloseAction(CloseAction action)
    {
        CloseAction = action;
        _settings.CloseAction = action;
        SettingsService.Save(_settings);
    }

    [RelayCommand]
    private void CopyAllLinks(Game? game)
    {
        if (game is null) return;

        var urls = game.GameFiles.Concat(game.Updates)
            .Where(f => !string.IsNullOrWhiteSpace(f.SourceUrl))
            .Select(f => GamesService.StripPkgQuery(f.SourceUrl))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (urls.Count == 0)
        {
            StatusText = "No download links available yet — wait for discovery.";
            StatusIsError = true;
            return;
        }

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, urls));
            StatusText = $"Copied {urls.Count} URL(s) — paste into IDM's batch download.";
            StatusIsError = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Copy failed: {ex.Message}";
            StatusIsError = true;
        }
    }

    [RelayCommand]
    private void RenameGame(Game? game)
    {
        if (game is null || string.IsNullOrWhiteSpace(game.TitleId)) return;
        _settings.GameDisplayNames[game.TitleId] = game.DisplayName ?? "";
        SettingsService.Save(_settings);
        StatusText = $"Renamed {game.TitleId} to '{game.EffectiveName}'.";
        StatusIsError = false;
    }

    partial void OnPortChanged(int value) => OnPropertyChanged(nameof(ProxyHint));

    partial void OnAllowSonyFallbackChanged(bool value)
    {
        _server.AllowSonyFallback = value;
        _settings.AllowSonyFallback = value;
        SettingsService.Save(_settings);

        StatusText = value
            ? "Sony fallback ALLOWED — PS4 downloads through this proxy."
            : "Sony fallback BLOCKED — missing files return 404.";
        StatusIsError = false;
    }

    public void Dispose()
    {
        // Dispose can be called from a thread-pool thread during shutdown;
        // DispatcherTimer.Stop is dispatcher-affine, so marshal if needed.
        try
        {
            if (_dispatcher.CheckAccess()) _tickTimer.Stop();
            else _dispatcher.Invoke(() => _tickTimer.Stop());
        }
        catch { /* ignore — process is exiting anyway */ }

        _server.RequestLogged -= OnRequestLogged;
        _server.Status -= OnServerStatus;
        _server.Dispose();
    }
}
