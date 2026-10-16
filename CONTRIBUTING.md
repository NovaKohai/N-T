# Contributing to NeoDM

NeoDM is an open source project created by unlimitedera and released under the MIT License.

## Ground rules
- Keep the code original. Do not paste code, assets or text from other projects unless the license allows it and it is credited.
- Be kind and constructive in issues and pull requests.

## Getting set up
Requirements: Windows 10/11, the .NET 8 SDK, Node.js, PowerShell, and (for the installer only) `dotnet tool install --global wix --version 5.0.2`.

```
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

`build.ps1` builds everything, runs every test suite, publishes to `dist\` and runs a forbidden-name scan. It must exit 0 before you open a pull request.

## Making a change
1. Open an issue first for anything bigger than a small fix.
2. Create a branch, make a focused change, and add or update tests.
3. Update `CHANGELOG.md` and bump the version as described in [docs/VERSIONING.md](docs/VERSIONING.md).
4. Run `build.ps1` and, if you touched the installer, `build-installer.ps1`.
5. Open a pull request describing what changed and how you tested it.

## Reporting bugs
Use the bug report template. Include your NeoDM version (Help, About), Windows version, browser (if relevant) and steps to reproduce. Never post cookies, passwords or private URLs.

## Project layout
See the "Project layout" section of the [README](README.md).
