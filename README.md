# PSX inject

A Windows desktop proxy + download helper for **PS4 and PS5**. Sit your PC between your console and PSN, **cache `.pkg` files locally**, **stream from Sony when you don't have them**, and **export clean download links** that you can paste straight into IDM (or any download manager) so you never have to re-download a game over the console's slow connection again.

Built with **WPF on .NET 10**, dark PlayStation-blue theme, MVVM. Ships as a **self-contained single-file `.exe`** — no .NET runtime install required on the target machine.

---

## Features

- **Forward HTTP proxy** that the console talks to instead of PSN directly. Listens on `0.0.0.0:<port>` so any device on your LAN can use it.
- **Local cache** — drop `.pkg` files into your data folder and the proxy serves them with full HTTP `Range` support, so the console thinks it's downloading from PSN at LAN speed and can resume downloads across reboots.
- **Sony fallback toggle** on the dashboard:
  - **🚫 Block** — cache misses return `404`. Use when you want to force manual cache prep.
  - **✓ Allow** — cache misses are streamed through to Sony's servers, so the console can finish downloads without intervention.
- **HTTPS tunneling** via the `CONNECT` method — PSN's auth, image, and notification APIs all need TLS, and the proxy relays them transparently.
- **Automatic part discovery** — the moment your console hits any `_0.pkg`, a background sweep walks `_1.pkg`, `_2.pkg`, … with HEAD probes (range `bytes=0-0`) until PSN says "no more". Each part's size lands in the UI before the console has even asked for it.
- **TMDB metadata** — when the console fetches a game's `tmdb2/.../<TitleId>.json`, the proxy quietly downloads it too and parses the **real game name, icon, background image, content ID, category** for the Games tab.
- **Games tab** with a card per detected title:
  - Background image (faded), 76px icon, real name, Title ID pill, category, total size, content ID.
  - Game files vs. updates listed separately, each with its size.
  - **📋 Copy all links** button → all parts (params stripped) joined by newlines, ready for IDM's batch import.
  - **📋 Copy URL** per part for one-off downloads.
- **Live request feed** with color-coded kinds: HIT (green), MISS (amber), PROXY (blue), SONY fallback (cyan), CONNECT tunnels, ERROR (red). Every row with a real URL is copyable; cache-miss URLs are auto-stripped of session-bound query tokens on copy.
- **Stat dashboard** — total requests, cache hits/misses, proxy requests, errors, bytes served, uptime, your machine's LAN IP(s) so you can type them into the console right from the hero card.
- **System tray icon** with a green-dot running indicator, tooltip status, and a context menu (Open / Start-Stop server / Exit).
- **Close-action dialog** with three choices (Stop & exit / Keep running in background / Cancel) and a "Don't ask again" remember-my-choice option.
- **Fault-tolerant by design**:
  - Single-instance mutex prevents double-launch.
  - Authoritative **port-conflict detection** — IPv4 wildcard + IPv4/IPv6 loopback probe-binds *and* an active TCP connect to localhost. If anything is bound to your chosen port, the UI surfaces a banner with a free suggested port and a one-click "Use port X" button.
  - **Firewall helper** — detects whether Windows Defender Firewall has an inbound rule for the exe; if not, a single click triggers UAC and adds it via `netsh` (no manual rule editing). On failure, the netsh log + a copy-pasteable manual command are surfaced in-app.
  - Per-connection errors never take down the listener.
  - Client-disconnect exceptions (the console cancelling parallel range requests mid-flight) are silently swallowed instead of polluting the error stream.
  - Global dispatcher / AppDomain / unobserved-task exception handlers — no silent crashes.
- **Smart shutdown** — server tear-down runs on a thread-pool thread so the UI never freezes during the close sequence.

---

## Installation

1. Grab the latest `PSX-Inject-vX.Y.Z-win-x64.exe` from the [Releases](https://github.com/ashrafeldawody/PSX-Inject/releases) page.
2. Run it. (Windows SmartScreen may warn the first time — click **More info → Run anyway**; the exe is unsigned.)
3. On first launch, click **🛡 Allow LAN access** in the firewall banner — one UAC prompt and you're done forever.

That's it. No installer, no admin install, no .NET runtime needed.

## Using it with your PS4 / PS5

1. Start the server in PSX inject (or it'll auto-start if you've enabled the option).
2. The Dashboard shows your **LAN IP : port** prominently — note it.
3. Set the proxy on your console:
   - **PS4** — *Settings → Network → Set Up Internet Connection → Use Wi-Fi / LAN → Custom*. When you reach the Proxy step, choose **Use** and enter your PC's IP and the port.
   - **PS5** — *Settings → Network → Settings → Set Up Internet Connection → choose your connection → ⋯ Advanced Settings → Proxy Server → Use*, then enter the IP and port.
4. Start (or pause + resume) a download on the console.
5. **If Sony fallback is ON**: the console downloads through your PC. Watch sizes accumulate on the Games tab in real time.
6. **If Sony fallback is OFF**: the first request 404s — copy the URL from the Requests tab, download via your favourite tool, drop the file into the data folder (without renaming), then resume the download on the console. It'll now serve from cache at LAN speed.

For multi-part games, hit the **📋 Copy all links** button on the game card after discovery completes — paste into IDM, hit download all, walk away.

---

## Settings

Stored as JSON at `%AppData%\PsxInject\settings.json`:

| Field                | Default                                          | Description                                                         |
| -------------------- | ------------------------------------------------ | ------------------------------------------------------------------- |
| `port`               | `8080`                                           | TCP port the proxy listens on                                       |
| `dataDirectory`      | `%USERPROFILE%\Documents\PsxInject\data`         | Where cached `.pkg` files live                                      |
| `closeAction`        | `Ask`                                            | What pressing × does: `Ask` / `RunInBackground` / `Exit`            |
| `autoStartServer`    | `false`                                          | Start the proxy automatically when the app launches                 |
| `allowSonyFallback`  | `false`                                          | Stream cache misses through Sony's servers (toggleable from the UI) |
| `requestTimeoutMs`   | `30000`                                          | Per-connection timeout                                              |
| `maxLogEntries`      | `500`                                            | Cap on the in-memory request feed                                   |

The file also stores discovered pkg metadata (sizes, source URLs, TMDB names/icons) so the Games tab is fully populated on relaunch.

---

## Building from source

Requirements: **Windows + .NET 10 SDK**.

```pwsh
git clone https://github.com/ashrafeldawody/PSX-Inject.git
cd PSX-Inject
dotnet build
```

Or open `PsxInject.sln` in Visual Studio 2022+ and press **F5**.

### Publishing a release-grade exe

```pwsh
dotnet publish PsxInject/PsxInject.csproj `
  -c Release -r win-x64 -o publish
```

Produces `publish/PsxInject.exe` — self-contained, single-file, compressed. Drop it on any Windows 10/11 x64 machine and it runs.

A **GitHub Actions release pipeline** is wired up: pushing a `v*` tag triggers a build that publishes the same exe and attaches it to a GitHub Release with auto-generated notes. See `.github/workflows/release.yml`.

---

## Project layout

```
.
├── PsxInject.sln
├── NuGet.config                 # forces nuget.org as the only source
├── .github/workflows/release.yml # build + release on tag push
└── PsxInject/
    ├── PsxInject.csproj
    ├── app.manifest             # DPI awareness, long-path support, Windows 10+ target
    ├── Assets/app.ico           # window/exe icon
    ├── App.xaml(.cs)            # entry, single-instance lock, global handlers, tray wiring
    ├── MainWindow.xaml(.cs)     # sidebar nav + 5 pages, close-confirm flow
    ├── Themes/                  # Colors.xaml, Styles.xaml (PS-blue dark theme)
    ├── Converters/              # XAML value converters
    ├── Server/                  # TcpListener proxy core, range serving, CONNECT tunnel
    ├── Models/                  # AppSettings, Game, GameMetadata, RequestLogEntry, ServerStats, CachedFile
    ├── Services/                # Settings, Network, Cache, Games, Firewall, TrayIcon
    ├── ViewModels/              # MainViewModel (CommunityToolkit.Mvvm)
    └── Views/                   # CloseConfirmDialog
```

---

## License

MIT
