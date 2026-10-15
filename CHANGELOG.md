# Changelog

All notable changes to NeoDM are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project uses [Semantic Versioning](https://semver.org/). The rule for keeping this file current is in `docs/VERSIONING.md`.

## [1.3.1] - 2026-10-16

### Fixed
- Dark mode: the tabs of the per-download progress window (Download status / Speed Limiter / Options on completion) and of the Options window (General / Save To / Downloads / Proxy / Torrents) were bright white or grey with unreadable text, and the selected tab was pure white. Tab headers are now themed in both themes: unselected = sidebar colour with muted text, hover = subtle highlight with full-contrast text, selected = surface colour with full-contrast text and an accent underline, themed keyboard focus ring, readable disabled state, themed content border. Tests render the headers in both themes (no near-white background) and check text contrast of at least 4.5:1 for selected, unselected and hover (3:1 for disabled).

### Added
- "Show this window when the download finishes": per-download checkbox in the progress window's "Options on completion" tab, and a global setting Options > Downloads > "Open the download window when a download finishes" (`ShowProgressOnCompletion`, on by default; settings schema 4 switches it on once for existing settings). When a download completes and its window was closed, hidden or minimised, the same single window for that download opens again (or is restored), comes to the front and shows the Complete state with Open / Open folder / Close. An already open window just shows Complete without taking focus. A download's own choice overrides the global one both ways; it is stored with the other per-download completion options (kept across restarts with "Remember this choice"). Downloads that were already complete when NeoDM started never open a window.
- Exactly one completion window: when this window is shown for a job it replaces the separate "download complete" dialog for that job. If the option is off for the job, the existing "Show download complete dialog" behaviour is unchanged; its greying rules for the other completion options (Exit NeoDM, Turn off computer) are unchanged too.
- "Change save location...": next to the read-only "Save To:" path of the "Options on completion" tab. It opens a folder picker (a themed text prompt if the system dialog is unavailable) and applies the folder to this download only, through the new `DownloadManager.MoveTo(id, folder)`. A finished download's file is moved (only that file). An unfinished download keeps its partial data in NeoDM's state folder (addressed by part id), so nothing partial has to move: the job's target folder is switched, a running transfer keeps running and finishes in the new folder, and the folder is created and write-tested first. A name already used in the new folder gets a unique name ("name (1).ext"); nothing is ever overwritten. A missing drive, a folder without write permission or an invalid path gives a clear message and leaves the job and files unchanged; choosing the current folder does nothing. The Save To text, the list and Properties follow. Torrent rows have the button disabled with a tooltip pointing to Options > Torrents.

### Changed
- Settings schema is now 4 (adds `ShowProgressOnCompletion`).

## [1.3.0] - 2026-10-15

### Fixed
- Browser capture now starts the download only in NeoDM. Before, the browser download was paused and cancelled only after NeoDM had acknowledged it, so it could keep going (or leave a file) while the options dialog was open. Now the extension cancels and erases the browser download (partial file, finished file and history entry) the moment it is created, and only then asks NeoDM. Chosen approach: `downloads.onCreated` plus `cancel` / `removeFile` / `erase`; no extra permission and no `onDeterminingFilename` or blocking `webRequest`, which would not make the cancel any earlier.
- Pressing Cancel in NeoDM's capture dialog now discards the download on purpose (the app reply carries `cancelled`). If NeoDM is unreachable or fails, the extension restarts the browser download from the same URL so nothing is lost.
- The capture dialog and the progress window of a captured download now open in front of the browser and every other window: foreground rights are passed from the native host to the app, then the window attaches to the foreground input queue, goes topmost briefly and, if Windows still refuses, the taskbar button flashes. The dialog is no longer owned by a minimised main window.
- Dark mode: menus and right-click menus no longer show white border or inner lines; menu items, separators, check marks, highlight and disabled text are fully themed in both themes.
- Dark mode: the sidebar and every other list, text box, combo box popup and grid use a thin themed scroll bar (rounded thumb, transparent track, hover and pressed states) instead of the Windows one.
- Dark mode: drop-down lists, combo boxes, tooltips and column headers are no longer bright white with unreadable text; hover, selected and disabled states have at least 4.5:1 text contrast (3:1 for disabled text), checked by tests for both themes.
- Scroll bars no longer touch or cover text boxes at the right edge of Options (all five tabs), Add download, the capture dialog, speed/schedule, Properties, progress and completion windows; content keeps a gutter wider than the scroll bar. Windows are limited to the usable screen area so nothing is clipped at 100%, 125% or 150% display scaling, and tall windows scroll instead of running off the screen.
- Options no longer has "Remember this choice for future sessions": what you pick under "When all downloads finish" in Options is saved permanently. The remember choice stays where a choice is temporary: the "Options on completion" tab of a download (unremembered per-download choices are no longer written to disk).

### Changed
- Completion sound is on by default. Existing settings are switched on once (settings schema 3); after that your choice in Options is kept. Each download can override it in its "Options on completion" tab (new "Play sound when done"), and a download muted there stays silent.
- Extension: capture of browser downloads is on by default for new installs and, once, for existing users who never chose (an explicit off is kept). The extension version follows the app (1.3.0).

### Added
- Extension: file-type preferences in the popup (Video, Audio, Images, GIF, Archives, Programs/installers, Documents, Other). They apply to the media ribbon and to download capture. Default: Video, Audio, Archives, Programs, Documents, Other on; Images and GIF off. Images and GIFs are only detected while ticked.
- Extension: session pause for "this site" or "all sites" (kept in `storage.session`, so it resets when the browser closes). While paused neither capture nor the ribbon trigger; the popup shows the state and has a one-click re-enable.
- Self-updater: NeoDM checks GitHub (`api.github.com/repos/unlimitedera/NeoDM/releases/latest`) 20 seconds after start and then every 24 hours, at most once per 12 hours across restarts, silently when offline or rate limited. A themed update window shows the release notes, "View full changelog" and "Release page" links and the buttons Update now, Remind me later and Skip this version. Update now downloads `NeoDM-Setup-*.msi`, verifies it against the release's `SHA256SUMS.txt` (refusing a mismatch, a missing entry or a file that is not an MSI) and runs `msiexec /i <msi> /passive` elevated; the installer closes and restarts NeoDM. Proxy settings from Options are used. See `docs/UPDATES.md`.
- Help > Check for updates, an About window with the GitHub repository link, the version and a "Check for updates" button, and Options > General > "Check for updates automatically" (on by default) with a way to forget a skipped version.
- `build-installer.ps1` also writes a versioned `NeoDM-Setup-<version>.msi` and `SHA256SUMS.txt` next to the MSI, ready to publish as a release the updater can use.

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
- Modern interface with light and dark themes.
- Chrome, Edge and Brave extension with a media ribbon for detected audio and video.
- System tray integration and start with Windows.
