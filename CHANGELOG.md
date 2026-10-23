## [1.2.2] - 2026-10-23

### Changed
- fixes

## [1.2.1] - 2026-10-23

### Changed
- Fixse

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

### Changed
- fixes

# Changelog

All notable changes to N&T are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/). The rule for keeping this file current is in `docs/VERSIONING.md`.

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
