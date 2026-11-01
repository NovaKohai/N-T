# Changelog

All notable changes to N&T are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/). The rule for keeping this file current is in `docs/VERSIONING.md`.

## [1.3.10] - 2026-11-01

### Changed
- الملخص العام: يركز هذا التحديث الضخم على هوية بصرية جديدة كلياً وتصميم أيقونة هندسية راقية، شاشة بدء تشغيل انسيابية تفاعلية، تحسينات جذرية في الأداء واستهلاك موارد الذاكرة والقرص (Zero-Copy Assembly & ArrayPool)، وإصلاح مشكلة رفض تنزيل روابط البث المباشر الموقّعة (403 Forbidden).

## [1.3.9] - 2026-10-31

### Added
- **Bespoke Desktop Icon Craft**: Replaced the previous icon with a bespoke, razor-sharp geometric monogram: an electric mint-teal speed 'N' seamlessly fused with a downward download velocity arrow against an obsidian squircle tile with subtle neon aura. Packed a true multi-tier 32-bit `NT.ico` with 7 full resolutions (16px to 256px), delivering pin-sharp clarity across Windows 10/11 taskbars, Alt+Tab, Start Menu, and High-DPI displays.
- **Asynchronous Startup Splash Screen**: Restructured the startup sequence to yield dispatcher frames immediately, enabling smooth card animations, live progressive status updates ("Restoring download queueâ€¦", "Connecting torrent clientâ€¦", "Ready"), and an intentional ~750ms minimum display threshold with seamless cross-fade into the main window.

### Changed
- **Smooth Pixel Scrolling**: Enabled `VirtualizingPanel.ScrollUnit="Pixel"` with 2-page lookahead caching on the downloads queue, delivering fluid 60/120 Hz scrolling through large download lists.
- **Adaptive Telemetry Throttling**: Implemented intelligent idle backoff on the main window refresh loop: telemetry updates run at 500ms during active transfers and gracefully relax to 1500ms when idle, saving background CPU cycles and laptop battery life while keeping instant 0-latency reaction to user actions.
- **Instant Zero-Copy Single-Stream Assembly**: Single-part and single-stream downloads now assemble via instant atomic filesystem renames instead of full disk re-reads, completing in sub-millisecond O(1) time and eliminating disk thrashing and duplicate SSD writes.
- **Large Object Heap (LOH) Elimination with `ArrayPool`**: Multi-part assembly buffers (4 MB) and streaming buffers now use `ArrayPool<byte>.Shared`, eliminating LOH memory fragmentation and expensive Gen 2 garbage collections.
- **High-Throughput Network `ReadAsync`**: Replaced per-read `CancellationTokenSource.CreateLinkedTokenSource` allocations with zero-allocation `WaitAsync` timeouts, removing thousands of CTS and timer allocations per second on gigabit downloads.
- **Single-Pass UI Queue Telemetry**: Collapsed multi-pass LINQ iterations over the downloads queue into a single allocation-free loop for active counts, download/upload speeds, and aggregate progress calculations.

### Fixed
- **HLS Stream Capture & Signed URLs**: Fixed an issue where stream manifest URLs (.m3u8) failed with HTTP 403 Forbidden because signature and timestamp query parameters were stripped by cache-busting filters. Manifests and URLs containing authentication signatures (`sig`, `sig2`, `token`, `hash`, etc.) now preserve all parameters intact.
- **Direct HLS & YouTube Pipeline**: Streamlined the download pipeline so HLS playlists and YouTube URLs bypass premature `Range: bytes=0-0` byte-range probes, routing directly to the dedicated HLS and yt-dlp handlers.
- **Range-Hostile Server Fallback**: Enhanced the download engine's pre-flight probe so servers and CDNs that reject `Range` headers with 400, 403, 405, or 501 gracefully fall back to single-stream GET instead of failing immediately.

## [1.3.8] - 2026-10-30

### Changed
- ðŸŒŸ Ø§Ù„Ù…Ù…ÙŠØ²Ø§Øª ÙˆØ§Ù„Ø¥Ø¶Ø§ÙØ§Øª Ø§Ù„Ø¬Ø¯ÙŠØ¯Ø© (Added)

## [1.3.7] - 2026-10-29

### Added
- **Toolbar Completion Action**: Added dedicated When done toolbar control right beside queue concurrency and speed limit buttons, allowing instant one-click selection and visibility of post-download actions.
- **Mini Widget Shutdown Toggle**: Added Turn off computer when done toggle directly in the floating mini widget context menu.
- **Accessibility (a11y) Full Audit & Remediation**: Audited all interactive surfaces, providing explicit AutomationProperties.Name and tooltips across the mini widget (close button, active count, telemetry speeds, aggregate progress), startup splash screen (AutomationProperties.LiveSetting='Polite' for screen readers), and main window action buttons (Import URLs, Resume all, Pause, Speed limit, Open file/folder, Empty state).

### Fixed
- **Sidebar Selection Color & Text Visibility**: Restructured NavItem control template so selection and hover overlays are placed behind text content with explicit theme ink foreground and semi-bold font weighting, eliminating invisible text on selected navigation filters.

## [1.3.6] - 2026-10-29

### Added
- Splash Screen: New native lightweight startup splash screen with high-DPI branding, live initialization status, progress bar, and user toggle in Settings.
- Micro-Interactions: Native GPU-friendly 120-180ms cubic transitions for dialogs, button hover/press overlays, and empty-state list cross-fades, fully respecting Windows ClientAreaAnimation.
- Mini Widget Enhancements: Added comprehensive desktop context menu (Open N&T, Add Download, Pause/Resume All, Hide Widget), drop hover feedback, and aggregate progress bar.

### Fixed
- Mini Widget: Fixed bidirectional visibility sync when closing via titlebar or context menu so settings and View menu stay in step.
- Mini Widget: Clamped floating coordinates against virtual desktop bounds to prevent off-screen positioning on multi-monitor changes.
- Mini Widget: Connected live aggregate percentage progress and upload speed telemetry.

## [1.3.5] - 2026-10-29

### Fixed
- Queue Engine: Fixed StopQueue so pending downloads in Queued status are properly paused rather than launched immediately by the background scheduler.
- Queue Engine: Fixed StartQueue to accurately account for both paused and queued jobs when resuming queue execution.
- Queue Scheduling: Fixed an issue where downloads added with speed limits or "Download later" were scheduled one year in the future, preventing them from starting.
- Queue Concurrency: Fixed browser capture dialog resetting MaxConcurrentDownloads to 3, preserving user-configured sequential queue concurrency limits.
- UI: Fixed inverted download row ordering in the main window table where new downloads were prepended, causing reverse execution order and backwards queue reordering.
- UI: Connected queue toolbar actions (Start Queue and Stop Queue) to pause and resume torrent transfers concurrently with standard downloads.

## [1.3.3] - 2026-10-28

### Added
- Ø§Ù„ØªØ­Ù‚Ù‚ Ù…Ù† Ø³Ù„Ø§Ù…Ø© Ø§Ù„ØªÙ†Ø²ÙŠÙ„Ø§Øª (Checksum): Ø¯Ø¹Ù… ÙØ­Øµ Ø§Ù„ØªØ¬Ø²Ø¦Ø© SHA-256 Ùˆ SHA-1 Ùˆ MD5 Ø£Ø«Ù†Ø§Ø¡ Ø§Ù„ØªØ¬Ù…ÙŠØ¹ Ù„Ù…Ù†Ø¹ Ø§Ù„ØªÙ„ÙØŒ Ù…Ø¹ Ø´Ø§Ø±Ø© Ø§Ù„ØªØ­Ù‚Ù‚ ÙÙŠ Ù†Ø§ÙØ°Ø© Ø§Ù„Ø§ÙƒØªÙ…Ø§Ù„.
- ÙÙƒ Ø§Ù„Ø¶ØºØ· Ø§Ù„Ø¢Ù…Ù† (Safe ZIP Extraction): ÙÙƒ Ø¶ØºØ· Ø¢Ù…Ù† ÙˆÙ…Ø­Ù…ÙŠ Ø¶Ø¯ Ù‡Ø¬Ù…Ø§Øª Ù…Ø³Ø§Ø± Ø§Ù„Ù…Ø¬Ù„Ø¯Ø§Øª Zip-Slip Ù…Ø¹ Ø®ÙŠØ§Ø± Ø§Ù„Ø§Ø³ØªØ®Ø±Ø§Ø¬ Ø§Ù„ØªÙ„Ù‚Ø§Ø¦ÙŠ.
- Ù†Ø§ÙØ°Ø© Ø§Ù„Ø³Ø±Ø¹Ø© Ø§Ù„Ù…ØµØºØ±Ø© Ø§Ù„Ø¹Ø§Ø¦Ù…Ø© (Mini Speed Widget): Ù†Ø§ÙØ°Ø© Ø¹Ø§Ø¦Ù…Ø© ÙÙˆÙ‚ Ø§Ù„ØªØ·Ø¨ÙŠÙ‚Ø§Øª Ù„Ø¹Ø±Ø¶ Ø§Ù„Ø³Ø±Ø¹Ø© Ø§Ù„Ø¥Ø¬Ù…Ø§Ù„ÙŠØ© ÙˆØ¹Ø¯Ø¯ Ø§Ù„ØªÙ†Ø²ÙŠÙ„Ø§ØªØŒ ÙˆØªØ¯Ø¹Ù… Ø§Ù„Ø³Ø­Ø¨ ÙˆØ§Ù„Ø¥ÙÙ„Ø§Øª Ù„Ù„Ø±ÙˆØ§Ø¨Ø· ÙˆÙ…Ù„ÙØ§Øª Ø§Ù„ØªÙˆØ±Ù†Øª.
- Ø¥Ø´Ø¹Ø§Ø±Ø§Øª ØªÙØ§Ø¹Ù„ÙŠØ© Ù…Ø®ØµØµØ© (Interactive Toast Balloon): Ø¥Ø´Ø¹Ø§Ø± Ø§ÙƒØªÙ…Ø§Ù„ Ø§Ù„ØªÙ†Ø²ÙŠÙ„ ÙŠØ­ØªÙˆÙŠ Ø¹Ù„Ù‰ Ø£Ø²Ø±Ø§Ø± Ø³Ø±ÙŠØ¹Ø© Ù„ÙØªØ­ Ø§Ù„Ù…Ù„Ù Ø£Ùˆ ÙØªØ­ Ø§Ù„Ù…Ø¬Ù„Ø¯ Ù…Ø¨Ø§Ø´Ø±Ø© Ø£Ùˆ Ø¥ØºÙ„Ø§Ù‚ Ø§Ù„Ø¥Ø´Ø¹Ø§Ø±.
- Ø¬Ø¯ÙˆÙ„Ø© Ø§Ù„Ù†Ø·Ø§Ù‚ Ø§Ù„ØªØ±Ø¯Ø¯ÙŠ Ø§Ù„ØªÙ„Ù‚Ø§Ø¦ÙŠØ© (Bandwidth Scheduling): ØªØ­Ø¯ÙŠØ¯ Ø£ÙˆÙ‚Ø§Øª Ø°Ø±ÙˆØ© Ù„ØªÙ‚ÙŠÙŠØ¯ Ø§Ù„Ø³Ø±Ø¹Ø© Ù†Ù‡Ø§Ø±Ø§Ù‹ ÙˆÙØªØ­ Ø§Ù„Ø³Ø±Ø¹Ø© Ø§Ù„ÙƒØ§Ù…Ù„Ø© Ù„ÙŠÙ„Ø§Ù‹ ØªÙ„Ù‚Ø§Ø¦ÙŠØ§Ù‹.
- Ø´Ø§Ø´Ø© Ù…ØªÙ‚Ø¯Ù…Ø© Ù„Ø¨ÙŠØ§Ù†Ø§Øª Ø§Ù„ØªÙˆØ±Ù†Øª (Torrent Telemetry): ØªØ¨ÙˆÙŠØ¨Ø§Øª Ù„Ù…Ø±Ø§Ù‚Ø¨Ø© Ø§Ù„Ù…ØªØªØ¨Ø¹Ø§Øª Trackers ÙˆØ§Ù„Ø£Ù‚Ø±Ø§Ù† Peers ÙˆØ³Ø±Ø¹Ø§ØªÙ‡Ù… Ù…Ø¹ Ø¥Ù…ÙƒØ§Ù†ÙŠØ© Ø¥Ø¶Ø§ÙØ© Trackers Ø¬Ø¯ÙŠØ¯Ø© Ø£Ø«Ù†Ø§Ø¡ Ø§Ù„ØªØ­Ù…ÙŠÙ„.
- ØªØ®Ø·ÙŠ Ø§Ù„Ø§Ø¹ØªØ±Ø§Ø¶ ÙÙŠ Ø§Ù„Ù…ØªØµÙØ­ (Alt-Click Bypass): Ø§Ù„Ù†Ù‚Ø± Ù…Ø¹ Ø§Ù„Ø¶ØºØ· Ø¹Ù„Ù‰ Ø²Ø± Alt Ù„ØªÙ†Ø²ÙŠÙ„ Ø§Ù„Ù…Ù„Ù Ø¹Ø¨Ø± Ø§Ù„Ù…ØªØµÙØ­ Ù…Ø¨Ø§Ø´Ø±Ø© Ø¯ÙˆÙ† Ø§Ø¹ØªØ±Ø§Ø¶Ù‡.
- Ù‚Ø§Ø¦Ù…Ø© Ø§Ù„Ù…ÙˆØ§Ù‚Ø¹ Ø§Ù„Ù…Ø³ØªØ«Ù†Ø§Ø© (Domain Blacklist): Ø§Ø³ØªØ«Ù†Ø§Ø¡ Ù†Ø·Ø§Ù‚Ø§Øª Ù…Ø­Ø¯Ø¯Ø© Ù…Ù† Ø§Ø¹ØªØ±Ø§Ø¶ Ø§Ù„ØªÙ†Ø²ÙŠÙ„Ø§Øª ÙˆØ§Ù„ÙˆØ³Ø§Ø¦Ø· Ø¹Ø¨Ø± Ù†Ø§ÙØ°Ø© Ø§Ù„Ø¥Ø¶Ø§ÙØ©.
- Ø§Ù„ØªÙ‚Ø§Ø· Ù…Ù„ÙØ§Øª Ø§Ù„ØªØ±Ø¬Ù…Ø© (Subtitle Sniffing): Ø§ÙƒØªØ´Ø§Ù ÙˆØªÙ†Ø²ÙŠÙ„ ØªØ¯ÙÙ‚Ø§Øª Ø§Ù„ØªØ±Ø¬Ù…Ø© WebVTT (.vtt) Ùˆ SubRip (.srt).

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
- UI: Added a direct "Refresh addressÃ¢â‚¬Â¦" action in DownloadProgressWindow and queue context menus to immediately update expired links and resume failed or paused downloads.
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
