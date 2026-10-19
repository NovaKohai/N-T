# AGENTS.md: N&T (NeoDM) Agent Playbook

> Read fully before touching code, tests, docs, or build scripts. **This file is itself scanned** by the forbidden-name gate.

## 1. Identity & Component Map

- Windows-only, MIT-licensed download manager + torrent client. .NET 8 engines + WPF app + MSI installer + Chromium MV3 extension.

| Path | Role |
|---|---|
| `src/NT.Core` | Pure .NET 8 HTTP(S) multi-connection engine; zero NuGet dependencies |
| `src/NT.App` | WPF desktop app; publishes `dist/app/NT.exe` |
| `src/NT.NativeHost` | Chrome native-messaging bridge; publishes `dist/host/NT.NativeHost.exe` |
| `src/NT.Torrent` | MonoTorrent 3.0.2 wrapper; `NT.Torrent.dll` must ship in `dist/app` |
| `extension/` | MV3 extension source (Chrome/Edge/Brave); packed into `dist/NeoDM.crx` |
| `installer/` | WiX 5.0.2 sources (`NT.wxs`, `configure.ps1`); outputs `artifacts\N&T-Setup.msi` |
| `worker/`, `public/` | Cloudflare Worker + landing page that serve update checks and installer downloads (see `docs/CLOUDFLARE_UPDATES.md`) |
| `tests/` | `NT.Tests`, `NT.Torrent.Tests`, Node extension suites, e2e helpers |

### Rebrand: legacy identifiers persist on purpose

The `NDM.*` to `NT.*` rename is committed, but these legacy identifiers remain for compatibility. Do not "fix" them; changing one side breaks the whole chain (extension ↔ host ↔ installer ↔ registry), and `README.md` plus other docs still describe the old `NDM.*` layout:

- Native-messaging host ids `com.ndm.bridge` **and** `com.nt.bridge` (both registered); `extension/background.js` still connects `com.ndm.bridge`.
- `dist/NeoDM.crx` filename (`pack-extension.cjs` also writes a best-effort `dist/N&T.crx` copy); `HKLM\SOFTWARE\NeoDM` registry keys; legacy `NeoDM-Setup*.msi` release copies.
- `%LOCALAPPDATA%\NDM` remains as a legacy fallback probe (e.g. yt-dlp search).
- `scripts/install.ps1` registers `com.ndm.bridge` and `com.nt.bridge` in HKCU for development; `build-installer.ps1` builds the MSI.

### State locations

- State root: `%LOCALAPPDATA%\N&T` (`App.VariantFolder`; quote the path in PowerShell, it contains `&`). Root holds `queue.json`, `queue.lock`, `job-completion.json`, settings and `updates\`; `torrent\` holds `torrents.json` plus fast-resume `cache\`.
- `NDM_STATE_DIR=<dir>` relocates *all* state and gives that instance its own single-instance mutex (`Local\NT.App.<hash12>`) and pipe (`NT.Downloads.<hash12>`). Set it for any manual run so a live queue is never touched.
- Legacy state lives in `%LOCALAPPDATA%\NDM`; never delete or migrate it automatically.
- Upgrades and reinstalls must never touch user data under `%LOCALAPPDATA%\N&T`, the legacy `%LOCALAPPDATA%\NDM`, or the user's download folders.

## 2. Forbidden-Name Scan: build fails hard

Every `build.ps1` run scans all source files, docs, markdown, scripts, tests, and first-party binaries. It **throws** (exit != 0) if it finds, case-insensitive:

1. the whole word `'I'+'DM'` (legacy 3-letter acronym)
2. the exact phrase `'Internet Download '+'Manager'`
3. the fragment `'q'+'bit'`

**Never write those strings anywhere**, not in code, comments, docstrings, commit messages, test fixtures, or docs. Refer to external tools generically as *"legacy download managers"* or *"third-party torrent clients"*.

Scan mechanics: dirs `node_modules`, `bin`, `obj`, `artifacts`, `keys`, `.git`, `payload` are skipped; `.exe`/`.dll` are only checked when the filename is first-party (`NT*`, `NeoDM*`, `NDM.*`, `NT.*`); text files over 20 MB are skipped. `dist/` is scanned, so keep generated text there clean too.

## 3. Build gate (no remote CI; `build.ps1` is the only validator)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1                       # full gate; must exit 0 before any PR
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -SkipTests            # fast publish while iterating on UI
powershell -NoProfile -ExecutionPolicy Bypass -File build-installer.ps1             # MSI; requires a build.ps1 run first
dotnet run --project tests/NT.Tests/NT.Tests.csproj -c Release                      # engine suite alone
dotnet run --project tests/NT.Tests/NT.Tests.csproj -c Release -- --benchmark       # loopback segmentation benchmark
dotnet run --project src/NT.App/Tests/Fidelity/NT.App.FidelityTests.csproj -c Release  # one WPF suite (swap <Area>: About, Advanced, CompletionActions, Fidelity, Manage, Options, OptionsDialog, Release, Themes, Ui; ProtocolTests lives in src/NT.App/Tests/)
dotnet run --project src/NT.NativeHost/NT.NativeHost.csproj -- --self-test          # native-host wire self-test
npm ci --prefix tests --ignore-scripts --no-fund --no-audit                         # required before the Node tests
node --test tests/extension-tests.cjs tests/extension-advanced-tests.cjs tests/extension-v130-tests.cjs tests/extension-v132-tests.cjs tests/extension-v133-tests.cjs tests/extension-v142-tests.cjs
node tools/pack-extension.cjs                                                       # re-sign dist/NeoDM.crx from extension/
```

All test projects are console executables: `dotnet run`, exit code is truth, not `dotnet test`. Scratch trees go under `%LOCALAPPDATA%\hermes\cache\scratch` and are created and deleted by the tests.

Gate order: (1) 13 C# test projects (including `NT.Torrent.Tests`), (2) native-host self-test, (3) Node extension suites, (4) publish `dist/app` + `dist/host` (self-contained `win-x64`), (5) copy an available `yt-dlp.exe` into `dist/app` if found, (6) pack CRX, (7) assert `NT.Torrent.dll` and `MonoTorrent.Client.dll` exist, (8) the forbidden-name scan runs last.

Gotchas:
- `build.ps1` force-kills processes named `NT`, `NeoDM`, `NT.NativeHost`, `NDM.NativeHost` before publishing, so a manually launched app instance is terminated mid-build.
- `extension/manifest.json` `"version"` is auto-synced from `Directory.Build.props` (and the CRX is repacked when it changes). Never edit it manually. The `-Torrent` switch exists and is a no-op kept for compatibility.
- Installer prerequisites: `dotnet tool install --global wix --version 5.0.2`, plus `wix extension add -g WixToolset.UI.wixext/5.0.2` and `wix extension add -g WixToolset.Util.wixext/5.0.2`. `build-installer.ps1` cross-checks `<Version>` against `extension/manifest.json` and the built `NT.exe` ProductVersion, so run the gate first. It outputs `artifacts\N&T-Setup.msi`, a versioned copy, legacy `NeoDM-Setup*.msi` copies, and `SHA256SUMS.txt` (the self-updater's release-asset format). When `public/updates` exists it also rewrites `version.json` and `SHA256SUMS.txt` there; the MSI itself is served through the Worker's `MSI_URL` variable (it is too large for static assets).

## 4. Version discipline

- `<Version>` in `Directory.Build.props` owns the product version; keep `AssemblyVersion`, `FileVersion`, `InformationalVersion` in step. Bump per `docs/VERSIONING.md` (patch = fix, minor = feature, major = breaking).
- Every change adds a top `CHANGELOG.md` heading `## [x.y.z] - YYYY-MM-DD` (Keep a Changelog sections). The extension tests assert the heading exists and the manifest matches `<Version>`.
- The rebrand reset the version to `1.0.0`, and subsequent updates increment from there (`1.0.1`).
- Commit subjects follow `<Product> <version>: <summary>` (see history, e.g. `N&T 1.0.0: complete rebrand, ...`).
- `TreatWarningsAsErrors` is enforced on `NT.Core` and `NT.Torrent` only.

## 5. Component quirks

### `src/NT.Core`
- Public contract (`DownloadManager`, `DownloadJob`, `DownloadRequest`) is documented in `src/NT.Core/README.md`; read it before changing any public type or semantics.
- Parallel ranges only after a validated `bytes=0-0` probe (exact `206` bounds plus a strong `ETag`, or a `Last-Modified` at least 60 s older than `Date`); otherwise a single stream. A `200` body is never appended to partial bytes.
- `DownloadOptions` bounds: `MaxConcurrentDownloads` 3 (1..16), `MaxSegments` 4 (1..32), `MinimumSegmentSize` 256 KiB (min 64 KiB), `RetryCount` 3 (0..8), `RequestTimeout` 30 s (100 ms..5 min). `SpeedBytesPerSecond` is an EWMA (2 s time constant, sampled about every 600 ms); `RemainingTime` is `null` for unknown lengths.
- `queue.json` is checkpointed (temp write + `File.Replace`) under an exclusive `queue.lock`; corrupt JSON fails closed and the engine refuses to reopen the queue until it is preserved/repaired manually.
- Partials live in a hidden `.ndm-staging` tree under the download directory; final moves never overwrite, collisions get `name (1).ext`.
- YouTube URLs are routed to external yt-dlp (`DownloadYoutubeAsync`): `--no-playlist --newline -f "bestvideo+bestaudio"` merged to `.mp4`. A missing yt-dlp fails with "Please install yt-dlp to download YouTube videos."
- HLS video downloads run through ffmpeg (`DownloadHlsAsync`); missing ffmpeg fails with "Please install FFmpeg to download streaming videos as MP4." Both are probed from `PATH` plus fixed locations (app dir, `%LOCALAPPDATA%\Programs`, `Program Files\ffmpeg\bin`, `C:\ffmpeg\bin`, chocolatey `bin`, WinGet packages; yt-dlp also `Program Files\N&T` and legacy `%LOCALAPPDATA%\NDM`). Not build dependencies, but the gate copies a locally found `yt-dlp.exe` into `dist/app`.

### `src/NT.App` (WPF)
- Engine `Changed` events arrive on thread-pool threads; `MainWindow` buffers them and applies updates on a 500 ms `DispatcherTimer` over stable row instances where only real value changes notify. Keep that pattern; do not poll the engine directly and do not marshal one event per network chunk.
- 9 themes (Light, Dark, System, Midnight, Graphite, Ocean, Forest, Rose, Paper) are resource dictionaries with identical key sets (the theme tests diff them). Shape keys (`ControlRadius`, `CardRadius`, ...) live at theme level; recoloring goes through `AccentFillBrush`. Custom accent colors must keep WCAG >= 4.5:1; contrast tests cover every theme plus extreme accents.
- `SmoothProgressBar` animates ~250 ms with `QuadraticEase EaseOut`; do not wire raw values into a jumped WPF `ProgressBar`.
- Singleton mutex is `Local\NT.App[.<state-suffix>]`; a second launch forwards via the control pipe. **UI tests must subclass `App` and override `OnStartup`** (the `TestApp` pattern in `src/NT.App/Tests/About/Program.cs` and other suites). Real startup grabs the machine mutex, collides with a live instance, shuts the test down, and unloads pack URIs (`UriFormatException: The URI prefix is not recognized`).
- `NEODM_SHOTS=<dir>` makes the theme suite save a populated main-window screenshot per theme (regenerates `docs/images/themes`).

### Native messaging & extension
- The pipe is `NT.Downloads` (`NativeBridge.PipeName`, `CurrentUserOnly`). Host-to-app traffic is single-line JSON + `\n`; browser-to-host stdio frames are the Chromium convention: 4-byte little-endian length + UTF-8 JSON, capped at 64 KiB.
- Decision window: no fixed timeout while pending frames flow. The host sends `{"pending":true}` every 15 s while the user decides (only when the request had `keepAlive:true`); the extension restarts the browser download only after 60 s of silence (re-armed by every frame) or an explicit failure. Older hosts get the legacy one-reply, 330 s limit.
- Extension id is pinned by the embedded key and recorded in `tools/extension-id.txt` (currently `kjfdpkddfacldnpgpikhllihdnoieobg`); a test verifies the id against `keys/neodm-extension.pem` whenever it exists on the machine, and verifies the CRX rebuilds reproducibly.
- `keys/neodm-extension.pem` is private and git-ignored (a test checks repo hygiene). If it is lost, the extension id changes and every registration must be redone. `tools/pack-extension.cjs` fails on id mismatch; `--init-key` generates a new key only when none exists.
- Chromium refuses drag-and-drop / silent third-party CRX installs on unmanaged machines ("not listed in the Chrome Web Store"). The supported route is **Load unpacked** (`extension/` for dev, `<install>\extension\unpacked` after install; `installer/install-extension.ps1` copies the path and opens the browser pages). Browser policy force-install (`INSTALL_POLICY=1`) only works on managed PCs.

### `src/NT.Torrent`
- `ParseMagnet` accepts only `magnet:?xt=urn:btih:` or `urn:btmh:` links, up to 8 KiB, without control characters; magnet metadata is downloaded via `AutoSaveLoadMagnetLinkMetadata = true`.
- `LoadTorrent` rejects `.torrent` files over `TorrentValidator.DefaultMaxTorrentFileBytes` (8 MiB), empty files, and unsafe names/paths: each segment must not be `.`/`..`, contain path separators or invalid filename characters, end in a dot or space, or use a reserved device name (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`).
- `EnsureInside` is the final guard: it re-checks every resolved per-file path (full, complete, incomplete) against the save directory before MonoTorrent writes.
- Fast-resume data is saved when a torrent stops (`AutoSaveLoadFastResume`); torrent state is `<state>\torrent\torrents.json` plus `torrents\` and `cache\` subdirs.

### Installer (`NT.wxs`)
- Per-machine MSI; UpgradeCode `6B1F0C7E-4A52-4C0B-9B0E-2D5C7A1E9F31` must never change. `AllowSameVersionUpgrades="yes"` permits same-version reinstalls during development.
- The MSI launches the app through Explorer (`explorer.exe <INSTALLFOLDER>\NT.exe`) so it runs with the normal user token, not `SYSTEM`: an elevated app breaks per-user pipes and drag-and-drop. `NO_LAUNCH=1` skips it.

## 6. Docs to keep in sync

- `src/NT.Core/README.md`: engine public contract (required reference).
- `docs/CHROME.md`: extension/native-messaging/browser policy; `docs/TORRENT.md`: torrent engine; `docs/DESIGN.md`: UI design (original artwork/code only, no third-party assets, matching `CONTRIBUTING.md` ground rules); `docs/UPDATES.md`: GitHub self-updater; `docs/CLOUDFLARE_UPDATES.md`: Cloudflare update endpoint and landing page; `docs/VERSIONING.md`: version/changelog rules.
- The app embeds `CHANGELOG.md` as a resource and shows it in the About window (Help menu), so changelog wording is user-facing.
