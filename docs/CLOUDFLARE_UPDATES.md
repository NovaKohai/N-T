# Cloudflare Update Server & Landing Page

N&T can serve update checks and distribute the installer from Cloudflare instead of the GitHub API.
The app tries the Cloudflare endpoint first (`UpdateChecker.DefaultCloudflareApiBase`) and falls back to
`api.github.com` when the endpoint is unreachable, so a deployment is optional, not required.

---

## What the Worker serves

`worker/index.js` runs in front of the `public/` static assets:

| Request | Answer |
|---|---|
| `GET /api/update` | The current release document (see below). |
| `GET /version.json` | Same document. |
| `GET /repos/NovaKohai/N-T/releases/latest` | Same document, so the client can use the GitHub API shape unchanged. |
| `GET /updates/*.msi` | Redirect to `MSI_URL` when the variable is set; otherwise a static asset, if present. |
| Everything else in `public/` | Served as a static asset (landing page, checksums, `updates/version.json`). |

The release document is GitHub's "latest release" JSON shape:

```json
{
  "tag_name": "v1.0.2",
  "name": "N&T 1.0.2",
  "body": "## [1.0.2] - ... (the matching CHANGELOG.md section)",
  "html_url": "https://github.com/NovaKohai/N-T/releases/tag/v1.0.2",
  "published_at": "2026-10-19T03:29:36Z",
  "assets": [
    { "name": "N&T-Setup-1.0.2.msi", "browser_download_url": "/updates/N&T-Setup-1.0.2.msi", "size": 93333021 },
    { "name": "SHA256SUMS.txt", "browser_download_url": "/updates/SHA256SUMS.txt", "size": 340 }
  ]
}
```

Asset URLs are stored relative; the Worker resolves them to absolute URLs against the request host,
so `workers.dev`, a custom domain and `wrangler dev` all work. The app only accepts `https` asset URLs
on GitHub, `*.workers.dev`, `*.pages.dev`, `*.r2.cloudflarestorage.com`, `*.cloudflare.com`, or the
configured endpoint host.

---

## The installer file

The MSI is about 93 MB, above the 25 MiB per-file limit for Workers static assets, so it is not part of
`public/`. Set the `MSI_URL` variable in Cloudflare (Workers dashboard: the Worker > Settings > Variables,
or a `vars` block in `wrangler.jsonc`) to a public URL of the installer:

* **Cloudflare R2**: upload the MSI to a bucket and expose it, then set `MSI_URL` to the bucket URL.
* **GitHub Releases**: set `MSI_URL` to
  `https://github.com/NovaKohai/N-T/releases/download/v1.0.2/N&T-Setup-1.0.2.msi`.

Without `MSI_URL`, update checks still work, but downloading the installer from `/updates/*.msi` returns
404 (the app then points the user at the release page). A small installer, under the asset limit, may be
placed directly in `public/updates/` instead; `public/updates/*.msi` is git-ignored for that reason.

---

## Deploying

Manual, from the project root:

```powershell
npx wrangler deploy
```

Or connect the repository in the Cloudflare dashboard (Workers & Pages > Create > Connect to Git);
Cloudflare reads `wrangler.jsonc` and deploys the Worker plus `public/` on every push.

Local preview: `npx wrangler dev`.

---

## Publishing a release

The release files are generated, never edited by hand. `build-installer.ps1` writes, from the built MSI:

* `artifacts\SHA256SUMS.txt` (also copied to `public\updates\SHA256SUMS.txt`) — SHA-256 of the installer
  in `hash *name` form, the format the self-updater verifies (see `docs/UPDATES.md`).
* `public\updates\version.json` — the release document above, with the matching CHANGELOG.md section as
  the body.

So the full release flow is:

1. Add a `## [x.y.z]` section to `CHANGELOG.md` and bump `<Version>` in `Directory.Build.props`
   (`docs/VERSIONING.md`).
2. Run `publish.bat` (`scripts/publish-update.ps1`). It refuses to run when the changelog section is
   missing, then runs the build gate, builds the MSI and release files, deploys with `wrangler deploy`,
   and optionally commits and tags.
3. Upload the new MSI to R2 / the GitHub release and update `MSI_URL` to point at it (the currently
   configured URL keeps working until then).

Steps 2 and 3 can also be done manually: `build.ps1`, `build-installer.ps1`, `npx wrangler deploy`.
