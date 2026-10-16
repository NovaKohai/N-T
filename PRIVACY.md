# Privacy policy

**NeoDM** (the Windows download manager) and **NeoDM Integration** (the browser extension) are open source
projects created by unlimitedera. Last updated: 2026-10-16.

## Summary
- NeoDM has no accounts, no analytics, no advertising and no telemetry.
- Nothing about you or your browsing is sent to the NeoDM author or to any server run by the project.
- The browser extension only talks to the NeoDM app installed on your own computer.

## The browser extension (NeoDM Integration)
**What it reads, locally in your browser:**
- The address of a download the browser starts, its file name and type, and the page it came from (so NeoDM can name the file and send the right referrer).
- The address of direct video or audio files found on pages you visit, so the media ribbon can offer a download.
- The address of the active tab, to offer a per-site pause.

**What it stores:** your settings (capture on/off, file types, ribbon on/off, cookie option) in the browser's extension storage, and a temporary per-session pause that is cleared when the browser closes.

**What it sends, and where:** the items above, and only when you download something, go to the NeoDM desktop app on your own computer through the browser's Native Messaging feature. They are never sent over the internet by the extension.

**Cookies:** off by default. If you turn on "Include authentication cookies", the browser asks for the cookies permission, and the cookies of the site you are downloading from are passed to the local NeoDM app for that download so sign-in downloads work. They are not logged and not sent anywhere else by the extension.

**Not collected:** passwords, page contents, browsing history, form data, or anything from private (incognito) windows.

## The desktop app (NeoDM)
- Your download list and settings are stored on your computer in `%LOCALAPPDATA%\NDM`. Proxy passwords are encrypted with Windows DPAPI for your account.
- The files you download come directly from the servers you chose (and, for torrents, from peers).
- **Update check:** NeoDM asks `api.github.com` for the latest release of this project (at start-up and about once a day) and downloads the installer from `github.com` when you choose Update now. GitHub sees the request like any web request (your IP address). Turn it off in Options, General, "Check for updates automatically".
- **Torrents:** as with every torrent client, peers and trackers can see your IP address while you share a torrent. Use the proxy settings if you want to hide it.

## Third parties
NeoDM does not share data with third parties. GitHub (updates) and the sites or peers you download from are the only outside parties your traffic reaches.

## Children
NeoDM is a general-purpose utility and does not knowingly collect data from anyone.

## Changes
Changes to this policy are published in this repository and listed in the changelog.

## Contact
Open an issue at https://github.com/unlimitedera/NeoDM/issues.
