# Changelog

All notable changes to NeoDM are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/). The rule for keeping this file current is in `docs/VERSIONING.md`.

## [1.2.1] - 2026-10-15

### Fixed
- Installer: setup failed with error 2762 while installing. The running-app check scheduled a script action before the install transaction started.

### Changed
- Installer: a running NeoDM (and its browser host) is now closed automatically, with no prompt and no manual step, both on install and on in-place upgrade. Unfinished downloads are interrupted and can be resumed.

## [1.2.0] - 2026-10-15

### Added
- Sidebar has a separate TORRENTS section (All torrents, Downloading, Seeding, Completed); the View menu mirrors it.
- Installer: NeoDM starts automatically after install and after upgrade, as the normal user (never elevated). `NO_LAUNCH=1` turns it off.
- Installer: NeoDM icon in Apps & Features, shortcuts and the setup wizard (banner and welcome/finish artwork generated from the icon).
- Settings schema marker (`SettingsSchema`) so one-time default changes can be migrated.

### Changed
- Category folders are on by default for new installs; existing settings that still carry the old default (off) are switched on once, after which your choice in Options is kept.
- The DOWNLOADS section of the sidebar shows only non-torrent items; torrents appear only under TORRENTS.
- Installing a newer MSI upgrades in place (same upgrade code); installing an older one is refused with a clear message. User data in `%LOCALAPPDATA%\NDM` is never touched; the startup entry, native host manifest/config and extension files are refreshed.

### Removed
- The "Connecting" sidebar filter (connecting items stay in All and Downloading).
- The flat "Seeding" and "Torrents" sidebar entries (replaced by the TORRENTS section).

### Fixed
- Add torrent dialog: clicking a file checkbox did not uncheck it (the three-state check box cycled back to checked), so files could only be deselected with Select none. File and folder boxes now toggle both ways; folders toggle their children and the summary and Start button follow.

## [1.1.0] - 2026-10-15

### Added
- Torrents are part of the normal product: magnet links and `.torrent` files, file selection, seeding, Torrents options tab. The installer registers NeoDM for `magnet:` links and `.torrent` files.
- Proxy support (HTTP, HTTPS, SOCKS5, optional credentials stored encrypted, test button).
- Remove dialog with an optional "also delete the file(s) from disk" choice.
- Right-click menu on downloads.
- Category folders: files can be saved to Programs, Compressed, Music, Video and Documents subfolders.
- Tabbed progress window with per-download completion actions.
- Completion actions for when downloads finish (shut down, restart, sleep, hibernate, sign out, run a program and more).
- Installer with a browser extension page (unpacked copy always installed, optional browser policy).
- About window showing the version, the creator and this changelog.
- Single product version and this changelog (`docs/VERSIONING.md`).

### Changed
- Options: the Connection tab was removed. Maximum simultaneous downloads, connections (segments) per download, retries, timeouts and dynamic segmentation now live in the Downloads tab, group "Connections". Existing settings files keep working.

## [1.0.0] - 2026-09-20

### Added
- Download queue with pause, resume, retry and scheduling.
- Segmented download engine with dynamic segmentation and resume.
- Windows 11 style interface with light and dark themes.
- Chrome, Edge and Brave extension with a media ribbon for detected audio and video.
- System tray integration and start with Windows.
