# NeoDM installer

Per-machine Windows Installer package (`artifacts\NeoDM-Setup.msi`, WiX 5). Elevates once (UAC), installs to `C:\Program Files\NeoDM`.

## Build
```
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2
wix extension add -g WixToolset.Util.wixext/5.0.2
powershell -File build-installer.ps1        # needs dist\app, dist\host, dist\NeoDM.crx (run build.ps1 first)
```
`build-installer.ps1 -AppExe NeoDM.exe` once the app exe is renamed (default `NeoDM.exe`). Prints the SHA-256.

## What it installs
- App, `host\NDM.NativeHost.exe` + `bridge-config.json` + `com.ndm.bridge.json` (allowed origin `chrome-extension://hohmmojlhbkelkobobpkochpdempcihd/`)
- `extension\NeoDM.crx` and `extension\unpacked\` (always kept), `LICENSE.txt` (MIT)
- Start Menu: NeoDM, "Install NeoDM browser extension"; optional desktop shortcut; Apps & Features entry
- Registers NeoDM for `magnet:` links and `.torrent` files (HKLM Capabilities / Open with; the default can still be changed in Windows; uninstall removes it)
- HKLM `Run\NeoDM` (startup), HKLM `NativeMessagingHosts\com.ndm.bridge` for Chrome, Edge, Brave

## Silent install
`msiexec /i NeoDM-Setup.msi /qn [INSTALLFOLDER="D:\NeoDM"] [RUN_AT_STARTUP=0] [DESKTOP_SHORTCUT=1] [INSTALL_POLICY=1] [NO_LAUNCH=1]`

## Upgrade, running app, launch
- **Upgrade:** a newer MSI with the same UpgradeCode upgrades in place (`MajorUpgrade`); an older one is refused ("A newer version of NeoDM is already installed..."). Settings and downloads state in `%LOCALAPPDATA%\NDM` are never touched. Host manifest, bridge config, update.xml, startup entry and extension files are rewritten on every install/upgrade.
- **Running app:** before files are touched, `a pre-install action closes NeoDM and its browser host automatically (no prompt, no manual step), also before the old version is removed in an upgrade. Interrupted downloads can be resumed.
- **Launch:** after install and after upgrade (not uninstall) setup starts NeoDM once through the user's Explorer shell, so it runs non-elevated in the interactive session. `NO_LAUNCH=1` skips it; it is also skipped when msiexec runs as SYSTEM (no interactive user).
- **Artwork:** icon from `src\NDM.App\Assets\NDM.ico` (Apps & Features, shortcuts); wizard banner/welcome bitmaps are generated from it by `build-installer.ps1`.

## Browser extension
Chrome, Edge and Brave refuse to auto-install a non-store extension on a PC that is not domain/MDM managed (verified: `chrome://policy` shows the forcelist entry as `[BLOCKED]`). So the default is policy-free: the finish page checkbox / Start Menu shortcut "Install NeoDM browser extension" opens the extension folder and each installed browser's extensions page; enable Developer mode and use **Load unpacked** on `extension\unpacked`.

`INSTALL_POLICY=1` (off by default) additionally writes `ExtensionInstallForcelist` value `7421` for each detected browser. It only takes effect on managed machines, and those browsers then show "Managed by your organization". Uninstall removes exactly that value (and any policy keys left empty), never other policies.


## Installer extension page
The wizard has a "Browser extension" page with an unchecked box "Also install through browser policy". Checking it is the same as `INSTALL_POLICY=1` (classic CRX/policy way; only effective on managed PCs). The unpacked folder and NeoDM.crx are always installed.

## Updates
NeoDM can update itself from GitHub releases by running this MSI (`msiexec /i <msi> /passive`, elevated). The installer already closes the running NeoDM and starts the new one, which is what makes the in-app update seamless. `build-installer.ps1` also writes `artifacts\NeoDM-Setup-<version>.msi` and `artifacts\SHA256SUMS.txt`; upload both to the release. The package is not Authenticode-signed: the updater relies on the SHA-256 in `SHA256SUMS.txt` from the same release (see `docs/UPDATES.md`). The only network traffic the app starts itself is the check to api.github.com and, on request, the download from github.com; the checkbox in Options > General turns the checks off.
