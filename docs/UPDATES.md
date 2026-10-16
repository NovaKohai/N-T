# Updates

NeoDM can tell you when a new release is on GitHub and install it for you. This page lists each step.

## What happens

1. Check. About 20 seconds after NeoDM starts, then every 24 hours while it runs, NeoDM sends one anonymous request:
   `GET https://api.github.com/repos/unlimitedera/NeoDM/releases/latest` with the header `User-Agent: NeoDM/<version>`. There is a 10 second timeout, no login and no cookies. NeoDM uses the proxy from Options > Proxy. The time of the last check is stored, so NeoDM asks at most once per 12 hours even if you restart it often (this also keeps NeoDM under GitHub's anonymous rate limit).
2. Compare. The release tag (`v1.2.3`) is compared with the running version as a semantic version (1.10.0 is newer than 1.9.0). Drafts, pre-releases and tags that are not plain `x.y.z` are ignored.
3. Offer. If the release is newer and you have not skipped it, an update window opens in front of your other windows: "NeoDM x.y.z is available (you have a.b.c)", the release notes, links to the full changelog and the release page, and the buttons Update now, Remind me later and Skip this version. Offline, rate limited (HTTP 403/429), a missing release (404) or an unreadable answer: nothing is shown for automatic checks.
4. Update now. NeoDM downloads `NeoDM-Setup-*.msi` and `SHA256SUMS.txt` from the release into `%LOCALAPPDATA%\NDM\updates`, with a progress bar, then:
   - computes the SHA-256 of the MSI and compares it with the entry for that file name in `SHA256SUMS.txt`; a mismatch, a missing entry, a missing `SHA256SUMS.txt` or a file that is not a Windows Installer package is refused, the file is deleted and nothing is run;
   - runs `msiexec /i <file> /passive` elevated. Windows shows its permission (UAC) prompt; if you decline, NeoDM tells you and nothing changes.
   The installer closes the running NeoDM and starts the new version when it finishes. Interrupted downloads can be resumed. Partial and old update files are removed on the next start.

You can also use Help > Check for updates... or the Check for updates button in Help > About NeoDM at any time. These always answer ("You are up to date" or the update window), even for a skipped version.

## Settings

- Options > General > Check for updates automatically is on by default. When it is off, NeoDM makes no update request on its own.
- Skip this version remembers the version. Automatic checks stay quiet until a newer one appears. Options shows the skipped version with a button to forget it.

## Security

- The MSI is not Authenticode-signed. NeoDM checks it against the SHA-256 published in the same GitHub release and fetched over HTTPS, so the update is only as trustworthy as that release. Only `https` addresses on `github.com` and `githubusercontent.com` are used for assets.
- NeoDM downloads and installs nothing until you press Update now.

## Privacy

The only network traffic NeoDM starts by itself is the check to `api.github.com` and, after you press Update now, the download from `github.com` (GitHub redirects to its release storage). GitHub sees your IP address and the `NeoDM/<version>` user agent, as with any web request. Switch the checkbox off to stop the checks. NeoDM sends no analytics and no identifiers.

## Publishing a release the updater can use

Upload these assets to the GitHub release (the tag must be `vX.Y.Z`):

- `NeoDM-Setup-X.Y.Z.msi` (`build-installer.ps1` writes it to `artifacts\`; the unversioned `NeoDM-Setup.msi` name is accepted too)
- `SHA256SUMS.txt` (written by `build-installer.ps1`; lines of the form `<sha256> *NeoDM-Setup-X.Y.Z.msi`)
