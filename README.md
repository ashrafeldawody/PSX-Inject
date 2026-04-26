# PSX inject — Windows Desktop (WPF)

A native Windows desktop port of the Node.js script in the parent folder.
Built with WPF on .NET 10, dark PlayStation-inspired theme, MVVM via
CommunityToolkit.Mvvm.

## Build

```pwsh
cd desktop
dotnet build
```

Open `PsxInject.sln` in Visual Studio for an IDE workflow (F5 to debug).

## Publish a single-file exe

```pwsh
cd desktop
dotnet publish PsxInject/PsxInject.csproj `
  -c Release -r win-x64 -o publish/win-x64
```

This produces `publish/win-x64/PsxInject.exe` — a self-contained
single-file binary that runs on any Windows 10/11 x64 machine without
the .NET runtime installed.

## How it works

- A `TcpListener` on `0.0.0.0:<port>` accepts inbound HTTP requests from the PS4.
- The request URL is matched against the same regex used by `index.js`:
  `^http://(?:gs2?\.ww\.|gst\.|gs\.)?(?:prod\.)?dl\.playstation\.net.*/([^?]+\.pkg)\?`
- **Match** → file is served from the configured data folder with full
  HTTP `Range` support (so the PS4 can resume large downloads).
- **No match** → the request is forwarded upstream via `HttpClient`, response
  streamed back to the client. Hop-by-hop headers are stripped on both sides.

## Settings

Stored as JSON at `%AppData%\PsxInject\settings.json`:

| Field                  | Default                                                  |
| ---------------------- | -------------------------------------------------------- |
| `port`                 | `8085`                                                   |
| `dataDirectory`        | `%USERPROFILE%\Documents\PsxInject\data`         |
| `autoStartServer`      | `true`                                                   |
| `minimizeToTrayOnClose`| `true`                                                   |
| `requestTimeoutMs`     | `30000`                                                  |
| `maxLogEntries`        | `500`                                                    |

## Fault tolerance

- **Single-instance lock** via named mutex.
- **Unhandled exceptions** on the dispatcher are caught and shown without crashing.
- **Per-connection** errors don't take down the listener.
- **Port conflict** is detected before starting — UI surfaces a clear message.
- **Auto-create** data directory if missing.

## Project layout

```
desktop/
├── PsxInject.sln
├── NuGet.config                 # forces nuget.org as the only source
└── PsxInject/
    ├── PsxInject.csproj
    ├── app.manifest             # DPI/long-path/Windows 10+ targeting
    ├── App.xaml(.cs)            # entry point, single-instance, global error handlers
    ├── MainWindow.xaml(.cs)     # sidebar + 4 pages
    ├── Server/                  # TcpListener proxy core (port of index.js)
    ├── Models/                  # AppSettings, RequestLogEntry, CachedFile, ServerStats
    ├── Services/                # SettingsService, NetworkService, CacheService
    ├── ViewModels/              # MainViewModel
    ├── Converters/              # XAML value converters
    └── Themes/                  # Colors.xaml + Styles.xaml (dark theme)
```

The original `../index.js` is kept as a reference implementation.
