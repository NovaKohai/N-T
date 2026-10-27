# Changelog

All notable changes to N&T are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/). The rule for keeping this file current is in `docs/VERSIONING.md`.

## [1.3.3] - 2026-10-28

### Changed
- التحقق من سلامة التنزيلات (Checksum): دعم فحص التجزئة SHA-256 و SHA-1 و MD5 أثناء التجميع لمنع التلف، مع شارة التحقق في نافذة الاكتمال.

## [1.3.2] - 2026-10-28

### Added
- Engine: Checksum verification support (SHA-256, SHA-1, MD5) on download requests and queue items with streaming hash validation and automatic integrity failure reporting.
- Engine: Safe ZIP extraction with Zip-Slip path traversal protection and optional automatic extraction upon completion.
- UI: Floating mini speed widget with live aggregate bandwidth, active job count, and drag-and-drop link/torrent importing.
- UI: Interactive completion toast balloon notification with direct "Open" and "Open folder" actions.
- UI: Bandwidth scheduler supporting time-based speed throttling windows with overnight wrap-around.
- Torrent: Telemetry tabs in TorrentDetailsWindow displaying live tracker status/announce intervals and connected peer metrics, plus manual tracker addition.
- Extension: Subtitle stream sniffing for WebVTT (.vtt) and SubRip (.srt) media manifests.
- Extension: Persistent domain blacklist configuration in popup to prevent interception on excluded sites.
- Extension: Alt-click bypass allowing native browser download handling when holding Alt key.

## [1.3.1] - 2026-10-27

### Changed
- Updates: Startup update checks now run 2 seconds after launch on every app start, removing the 12-hour launch cooldown so available updates prompt immediately while respecting the "Check for updates automatically" option.

## [1.3.0] - 2026-10-27

### Added
- Reliability: Added pre-download, pre-assembly, and torrent volume capacity verification to prevent drive exhaustion and partial staging corruption.
- Engine: Implemented smart resume disk space calculation that checks only remaining un-downloaded bytes instead of total file size, allowing partial downloads to resume safely.
- UI: Added a direct "Refresh addressâ€¦" action in DownloadProgressWindow and queue context menus to immediately update expired links and resume failed or paused downloads.
- UI: Added live free disk space telemetry to the main window status bar alongside aggregate transfer rates.
- Torrent: Added interactive context menu to TorrentDetailsWindow file list supporting live priority changes (High, Normal, Low, Skip) and one-click file and folder opening.

### Fixed
- Torrent: Guarded file opening in TorrentDetailsWindow to require 100% completion before launching, preventing execution of sparse or incomplete placeholder files.
- Reliability: Hardened bulk import URI evaluation with safe parsing to prevent dialog crashes on malformed or non-standard addresses.

## [1.2.9] - 2026-10-26

### Fixed
- File Assembly: Eliminated cross-volume post-download file copying by placing intermediate assembly staging on the destination volume, enabling near-instantaneous completion and preventing excessive disk writes.
- File Assembly: Increased sequential file assembly stream buffer from 64 KiB to 4 MiB, drastically accelerating part stitching on SSD and HDD storage.
- UI Feedback: Added clear "Assembling file..." status, disk writing indicator, and live assembly progress telemetry so multi-gigabyte downloads provide continuous visual feedback instead of appearing frozen at 100%.

## [1.2.8] - 2026-10-24

### Changed
- Queue Management: Enhanced queue concurrency controls, live reordering, and queue start/stop automation.

### Fixed
- Engine: Eliminated race condition and redundant cancellation churn during dynamic shard splitting when multiple connection slots are available simultaneously.

## [1.2.7] - 2026-10-24

### Added
- Host Link Generator: Dedicated tool window and automatic link resolution for supported file hosts (datanodes.to, fuckingfast.co).

### Fixed
- Engine: Resolved dynamic shard truncation retry offset verification during multi-connection downloads.

## [1.2.6] - 2026-10-24

### Added
- Host Link Generator: Added dedicated host link generator tool window under Tasks and Downloads menus to convert batches of `datanodes.to` and `fuckingfast.co` links into direct download links with one-click queue addition and clipboard copying.
- Automatic Host Link Resolution: Integrated `HostLinkResolver` into `BulkImportWindow` and `AddDownloadWindow` so pasted host links are automatically resolved into direct mirror download links.
- Downloadable Link Detection for Hosters: Integrated `HostLinkResolver` into `DownloadableLinkDetector` so copied `datanodes.to` and `fuckingfast.co` URLs are recognized by the clipboard monitor and import tools.

## [1.2.5] - 2026-10-24

### Added
- Queue Concurrency Controls: Added live simultaneous download controls (1 to 16 at a time) directly accessible from the main toolbar button and queue context menus.
- Download Later in Add URL: Added a dedicated "Download later" action in the Add download dialog to queue downloads in paused status without immediate network transfer.
- Queue Order Reordering: Added "Move up in queue" and "Move down in queue" actions with keyboard shortcuts (Alt+Up, Alt+Down) to reorder queued downloads.
- Start and Stop Queue Actions: Added dedicated toolbar buttons and menu commands to start queue processing or pause all active transfers.
- Concurrency Selection on Link Addition: Added queue concurrency selection in both Add Download and browser capture dialogs.

### Fixed
- Queue Concurrency Live Update: Ensured that changing concurrent downloads in Settings or toolbar updates the running engine immediately without restart.
- Quiet Browser Add-to-Queue: Fixed browser capture dialog so selecting "Add to queue" adds the job quietly without opening a popup progress window.

## [1.2.4] - 2026-10-24

### Fixed
- Torrent: Fixed view filter routing so newly added torrents display immediately in the list rather than being hidden by the default download filter.
- Torrent: Fixed context menu sequential download toggle and prevented ID mismatch exceptions.
- Torrent: Optimized magnet link addition by retaining resolved metadata bytes and eliminating redundant second metadata network requests.
- Torrent: Enabled file and folder opening for completed multi-file torrent downloads.
- Torrent: Enhanced announce URL extraction and tracker merging from magnet links.
- Torrent: Added immediate UI queue updates on torrent state and speed changes.
- Torrent: Implemented batch file priority updates to eliminate repeated disk I/O when adding torrents with skipped files.
## [1.2.3] - 2026-10-23

### Fixed
- Engine: Resolved IPv4/IPv6 socket address family mismatch (WSAEADDRNOTAVAIL 10049) on Windows loopback connections in proxy and HTTP handlers.
- UI: Added accessible names and tooltips across the main toolbar, queue progress indicators, and status summary.
- UI: Respected system-level reduced motion preferences (`ClientAreaAnimation`) in progress bar smoothing animations.
- UI: Clarified update check frequency and server description in options.
- Extension: Added light mode theme support (`prefers-color-scheme`) and improved contrast in popup controls.

## [1.2.2] - 2026-10-23

### Fixed
- Engine: Resolved IPv4/IPv6 socket address family mismatch (WSAEADDRNOTAVAIL 10049) on Windows loopback connections in proxy and HTTP handlers.
- UI: Added accessible names and tooltips across the main toolbar, queue progress indicators, and status summary.
- UI: Respected system-level reduced motion preferences (`ClientAreaAnimation`) in progress bar smoothing animations.
- Extension: Added light mode theme support (`prefers-color-scheme`) and improved contrast in popup controls.

## [1.2.1] - 2026-10-23

### Fixed
- Streaming: Prevented infinite looping and redundant segment requests when capturing live HLS media streams.
- Streaming: Improved stream termination detection for continuous live broadcasts.

## [1.2.0] - 2026-10-20

### Added
- Download in background (system tray): dedicated button in the download progress dialog and context menu allowing users to send active downloads to the notification area under the clock to continue transfers silently in the background.
- Tray progress restoration: clicking the notification area tray icon or selecting the active download in the tray menu instantly restores the progress dialog.
- Background download completion notices: native balloon notifications announce when a background download completes.

## [1.1.0] - 2026-10-20

### Added
- Batch URL pattern expansion: syntax `[01-20]` and `[a-z]` with zero padding, step support, and Cartesian product generation in Add URL and Bulk Import.
- Global bandwidth limiter: aggregate rate governor across concurrent HTTP downloads with quick toolbar presets and custom limits.
- Sequential torrent downloading: piece order prioritization for previewing media streams during download.
- Checksum and file integrity verifier: streaming verification supporting SHA-256, SHA-1, and MD5 with real-time match validation in download properties and completion windows.
- Clipboard monitor and link auto-capture: background clipboard listener for downloadable file links and torrent magnets with instant add prompts.
- Firefox WebExtension and native bridge: dual manifest support with Gecko integration and native messaging registration.

## [1.0.3] - 2026-10-20

### Fixed
- Core: Fixed connection retry backoff timing and proxy authentication state handling.

## [1.0.2] - 2026-10-19

### Changed
- Desktop: one 20 DIP dialog heading scale and 13 DIP window text, flat accent fills (no gradient), and a primary button that uses the dedicated accent hover token.
- Desktop: Add URL is the single primary toolbar action, toolbar groups are separated, the sidebar actions share one width and height, and search shows a visible placeholder.
- Desktop: Options uses one 32 DIP control height and drops the redundant Appearance shortcut; progress details no longer use ASCII arrows, and action labels follow sentence case.
- Extension: the media ribbon and the popup media list share the same rules and offer standard video resolutions from 480p up to 8K (the popup previously stopped at 1080p).
- Extension: the media ribbon's minimize, close and download buttons meet the 24 px minimum target size for easier clicking and touch input.
- Extension: popup, ribbon and background worker now use one shared media helper module (titles, sizes, qualities, estimates).

## [1.0.1] - 2026-10-18

### Added
- Automated update delivery via Cloudflare Workers with one-click publisher script (`publish.bat`).
- Parallel segmented HLS stream downloader with AV1 and fMP4 remuxing.
- Real-time download progress smoothing and live speed / ETA calculation.

### Fixed
- Window layout and details pane sizing in download progress dialog.
- UI layout stability during download initialization.

## [1.0.0] - 2026-09-20

### Added
- Download queue with pause, resume, retry and scheduling.
- Segmented download engine with dynamic segmentation and resume.
- Modern interface with light and dark themes.
- Chrome, Edge and Brave extension with a media ribbon for detected audio and video.
- System tray integration and start with Windows.
