# AGENTS.md — N&T (NeoDM) Agent Playbook

> Single source of truth for AI agents and maintainers working on the **NeoDM** codebase (user-facing brand: **N&T**). Read fully before touching code, tests, docs, or build scripts.

## 1. Identity & Component Map

- Windows-only, MIT-licensed download manager + torrent client. .NET 8 engines + WPF app + Win32 installer + Chromium MV3 extension.
- Verify paths against `build.ps1` and the `.csproj` files — docs (including `README.md`) still describe the pre-rebrand `NDM.*` layout.

| Path | Role |
|---|---|
| `src/NT.Core` | Pure .NET 8 HTTP(S) multi-connection engine; zero NuGet dependencies |
| `src/NT.App` | WPF desktop app; publishes `dist/app/NT.exe` |
| `src/NT.NativeHost` | Chrome native-messaging bridge; publishes `dist/host/NT.NativeHost.exe` |
| `src/NT.Torrent` | MonoTorrent 3.0.2 wrapper; `NT.Torrent.dll` must ship in `dist/app` |
| `extension/` | MV3 extension source (Chrome/Edge/Brave); packed into `dist/NeoDM.crx` |
| `installer/` | WiX 5.0.2 sources (`NT.wxs`, `configure.ps1`) → `artifacts\N&T-Setup.msi` |
| `tests/` | `NT.Tests`, `NT.Torrent.Tests`, Node extension suites, e2e helpers |

### Rebrand in progress (uncommitted rename `NDM.*` → `NT.*`)

Legacy identifiers are kept on purpose for compatibility. **Do not "fix" them** — changing one side breaks the whole chain (extension ↔ host ↔ installer ↔ registry):

- Native-messaging host ids `com.ndm.bridge` **and** `com.nt.bridge` (both registered); `extension/background.js` still connects `com.ndm.bridge`.
- `dist/NeoDM.crx` filename; `HKLM\SOFTWARE\NeoDM` registry keys; legacy `NeoDM-Setup*.msi` release copies.
- `%LOCALAPPDATA%\NDM` appears as a legacy fallback probe (e.g. yt-dlp search).
- `scripts/install.ps1` still expects pre-rebrand exe names and fails as-is; use `build-installer.ps1` instead.

### State locations

- State root: `%LOCALAPPDATA%\N&T` (`App.VariantFolder` — quote this path in PowerShell, it contains `&`). Root holds `queue.json`, `queue.lock`, `job-completion.json`, settings and `updates\`; `torrent\` holds `torrents.json` + fast-resume `cache\`.
- **`NDM_STATE_DIR=<dir>` env var is the safe way to launch the app**: it relocates *all* state and gives that instance its own single-instance mutex (`Local\NT.App.<hash12>`) and pipe (`NT.Downloads.<hash12>`). Always set it when manually testing so a live queue is never touched.
- Legacy state lives in `%LOCALAPPDATA%\NDM`; never delete or migrate it automatically.

## 2. Forbidden-Name Scan — build fails hard

Every `build.ps1` run scans all source files, docs, markdown, scripts, tests, and first-party binaries and **throws** if it finds (case-insensitive):

1. the whole word `'I'+'DM'` (legacy 3-letter acronym);
2. the exact phrase `'Internet Download '+'Manager'`;
3. the fragment `'q'+'bit'`.

Never write those strings anywhere — code, comments, docstrings, commit messages, test fixtures, or docs. **This file is itself scanned.** Refer to external tools generically as *"legacy download managers"* or *"third-party torrent clients"*.

Scan mechanics: dirs `node_modules`, `bin`, `obj`, `artifacts`, `keys`, `.git`, `payload` are skipped; `.exe`/`.dll` are only checked when the filename is first-party (`NT*`, `NeoDM*`, `NDM.*`, `NT.*`); text files > 20 MB are skipped. `dist/` **is** scanned — keep generated text there clean too.

## 3. Build Gate (no remote CI — `build.ps1` is the only validator)

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

All test projects are **console executables** (`dotnet run`, exit code is truth — not `dotnet test`). Scratch trees go under `%LOCALAPPDATA%\hermes\cache\scratch` and are created/deleted by the tests.

Gate order: 13 C# test projects (including `NT.Torrent.Tests`) → native-host self-test → Node extension suites → publish `dist/app` + `dist/host` (self-contained `win-x64`) → copy an available `yt-dlp.exe` into `dist/app` if found → pack CRX → assert `NT.Torrent.dll` + `MonoTorrent.Client.dll` exist → **forbidden-name scan last**.

Gotchas:
- `build.ps1` force-kills processes named `NT`, `NeoDM`, `NT.NativeHost`, `NDM.NativeHost` before publishing — a manually launched app instance will be terminated mid-test.
- `extension/manifest.json` `"version"` is auto-synced from `Directory.Build.props` (CRX repacked when it changes). **Never edit it manually.** The `-Torrent` switch exists and is a no-op kept for compatibility.
- Installer prereqs: `dotnet tool install --global wix --version 5.0.2`, plus `wix extension add -g WixToolset.UI.wixext/5.0.2` and `wix extension add -g WixToolset.Util.wixext/5.0.2`. `build-installer.ps1` cross-checks `<Version>` vs `extension/manifest.json` vs the built `NT.exe` ProductVersion — run the gate first. Outputs `artifacts\N&T-Setup.msi`, a versioned copy, legacy `NeoDM-Setup*.msi` copies, and `SHA256SUMS.txt` (release-asset format expected by the self-updater).

## 4. Version Discipline

- `<Version>` in `Directory.Build.props` is the single source of truth; keep `AssemblyVersion`, `FileVersion`, `InformationalVersion` in step. Bump per `docs/VERSIONING.md` (patch = fix, minor = feature, major = breaking).
- Every change: add a top `CHANGELOG.md` heading `## [x.y.z] - YYYY-MM-DD` (Keep a Changelog sections). The extension tests assert the heading exists and the manifest matches `<Version>`.
- **The rebrand reset the version to `1.0.0`**, but `CHANGELOG.md` still shows the pre-rebrand 1.1.0–1.4.2 history above it. Do not infer "current version" from CHANGELOG order — read `Directory.Build.props`.
- Commit subjects follow `<Product> <version>: <summary>` (see history, e.g. `N&T 1.0.0: complete rebrand, ...`).
- `TreatWarningsAsErrors` is enforced on `NT.Core` and `NT.Torrent` only.

## 5. Component Quirks (things filenames don't tell you)

### `src/NT.Core`
- Public contract (`DownloadManager`, `DownloadJob`, `DownloadRequest`) is documented in `src/NT.Core/README.md` — read it before changing any public type or semantics.
- Parallel ranges only after a validated `bytes=0-0` probe (exact `206` bounds + strong `ETag`, or `Last-Modified` ≥ 60 s older than `Date`); otherwise a single stream. Never append a `200` body to partial bytes.
- `DownloadOptions` bounds: `MaxConcurrentDownloads` 3 (1..16), `MaxSegments` 4 (1..32), `MinimumSegmentSize` 256 KiB (min 64 KiB), `RetryCount` 3 (0..8), `RequestTimeout` 30 s (100 ms..5 min). `SpeedBytesPerSecond` is an EWMA (2 s time constant, ~600 ms samples); `RemainingTime` is `null` for unknown lengths.
- `queue.json` is checkpointed (temp write + `File.Replace`) under an exclusive `queue.lock`; corrupt JSON **fails closed** and must be manually preserved/repaired — the engine refuses to reopen the queue.
- Partials live in a hidden `.ndm-staging` tree under the download directory (target volume); final moves never overwrite — collisions get `name (1).ext`.
- **YouTube URLs are routed to external yt-dlp** (`DownloadYoutubeAsync`): `--no-playlist --newline -f "bestvideo+bestaudio"` merged to `.mp4`; optional ffmpeg is passed via `--ffmpeg-location`. Discovery: app dir, `PATH`, `%LOCALAPPDATA%\Programs`, `Program Files\N&T`, WinGet packages. No yt-dlp → the download fails with an explicit "please install yt-dlp" message. It is not a build dependency, but the gate copies a locally-found `yt-dlp.exe` into `dist/app`.

### `src/NT.App` (WPF)
- Engine `Changed` events arrive on thread-pool threads; `MainWindow` buffers them and applies updates on a **500 ms `DispatcherTimer`** over stable row instances (only real value changes notify). Keep that pattern — do not poll the engine directly and do not marshal one event per network chunk.
- 9 themes (Light, Dark, System, Midnight, Graphite, Ocean, Forest, Rose, Paper) are resource dictionaries with **identical key sets** (ThemeTests diffs them). Theme shape keys (`ControlRadius`, `CardRadius`, …) are theme-level; recolor goes through `AccentFillBrush`. Custom accent colors must keep WCAG ≥ 4.5:1 — contrast tests cover every theme plus extreme accents.
- `SmoothProgressBar` animates ~250 ms with `QuadraticEase EaseOut`; do not wire raw progress values to a jumped copy.
- Singleton mutex is `Local\NT.App[.<state-suffix>]`; a second launch forwards via the control pipe. **UI tests must subclass `App` and override `OnStartup`** (the `TestApp` pattern in `src/NT.App/Tests/About/Program.cs` and other suites): running real startup grabs the machine mutex, collides with any live instance, shuts the test process down and unloads pack URIs (`UriFormatException: The URI prefix is not recognized`).
- `NEODM_SHOTS=<dir>` env var makes the theme suite save a populated main-window screenshot per theme (regenerates `docs/images/themes`).

### Native messaging & extension
- Pipe is `NT.Downloads` (`NativeBridge.PipeName`, `CurrentUserOnly`). Desktop→app traffic is single-line JSON + `\n`; browser→host stdio frames are the Chromium convention: 4-byte little-endian length + UTF-8 JSON, capped at 64 KiB.
- Decision window: no fixed timeout while pending frames flow. The host sends `{"pending":true}` every 15 s while the user decides (only when the request had `keepAlive:true`); the extension restarts the browser download only after 60 s of silence (re-armed by every frame) or an explicit failure. Older hosts get the legacy one-reply, 330 s limit.
- Extension id is pinned by the embedded key and recorded in `tools/extension-id.txt` (currently `kjfdpkddfacldnpgpikhllihdnoieobg`); a test verifies the id against the key file whenever `keys/neodm-extension.pem` exists on this machine and that the CRX rebuilds reproducibly.
- `keys/neodm-extension.pem` is private and git-ignored (a test verifies repo hygiene). If it is lost, the extension id changes and every registration must be redone. `tools/pack-extension.cjs` fails on id mismatch; use `--init-key` only when starting a completely fresh key.
- Chromium refuses drag-and-drop / silent third-party CRX installs on unmanaged machines ("not listed in the Chrome Web Store"). The supported route is **Load unpacked** (`extension/` for dev, `<install>\extension\unpacked` after install; `installer/install-extension.ps1` copies the path and opens the browser pages). Browser policy force-install (`INSTALL_POLICY=1`) only works on managed PCs.

### `src/NT.Torrent`
- `TorrentValidator` keeps every torrent strictly inside the save directory: rejects `..`, absolute paths, alternate data streams (`:stream`), reserved Windows device names; `.torrent` files larger than 8 MiB are rejected; magnets pre-fetch metadata (DHT/PEX) before any disk structure.
- Fast-resume data is saved with `AutoSaveLoadFastResume` and written on stop; torrent state is `<state>\torrent\torrents.json` (+ `torrents\` and `cache\` subdirs).

### Installer (`NT.wxs`)
- Per-machine MSI; UpgradeCode `6B1F0C7E-4A52-4C0B-9B0E-2D5C7A1E9F31` must never change. `AllowSameVersionUpgrades="yes"` permits same-version re-installs during development.
- **Upgrade/uninstall must never touch user data** under `%LOCALAPPDATA%\N&T` (or legacy `%LOCALAPPDATA%\NDM`) or the user's download folders.
- The MSI launches the app through Explorer (`explorer.exe <INSTALLFOLDER>\NT.exe`-style custom action) so it runs with the normal user token, not `SYSTEM` — an elevated app breaks per-user pipes and drag-and-drop. `NO_LAUNCH=1` skips it.

## 6. Docs to keep in sync

- `src/NT.Core/README.md` — engine public contract (required reference).
- `docs/CHROME.md` — extension/native-messaging/browser policy; `docs/TORRENT.md` — torrent engine; `docs/DESIGN.md` — UI design (original artwork/code only — no third-party assets, matching `CONTRIBUTING.md` ground rules); `docs/UPDATES.md` — GitHub self-updater; `docs/VERSIONING.md` — version/changelog rules.
- The app embeds `CHANGELOG.md` as a resource and shows it in Help → About — changelog wording is user-facing.
