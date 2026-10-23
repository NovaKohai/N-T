/**
 * N&T (NeoDM) update endpoint.
 *
 * The release of record is `public/updates/version.json`, written by build-installer.ps1
 * next to the built MSI. The desktop client asks for a GitHub-style "latest release"
 * document, so the same JSON is served under those aliases, with asset URLs resolved
 * to absolute ones against the request host.
 *
 * Environment:
 *   MSI_URL  Public URL of the installer (R2 bucket, GitHub release asset, ...). The
 *            93 MB MSI is too large for Workers static assets, so when this is set,
 *            /updates/*.msi requests are redirected there.
 */

const RELEASE_DOCUMENT = "/updates/version.json";
const UPDATE_ALIASES = new Set([
  "/api/update",
  "/version.json",
  "/repos/NovaKohai/N-T/releases/latest",
]);

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (UPDATE_ALIASES.has(url.pathname)) {
      return releaseDocument(url, env);
    }

    if (url.pathname.startsWith("/updates/") && url.pathname.toLowerCase().endsWith(".msi")) {
      if (env.MSI_URL) {
        return Response.redirect(env.MSI_URL, 302);
      }
      const rawFilename = url.pathname.split("/").pop();
      const filename = decodeURIComponent(rawFilename);
      const match = filename.match(/(?:Setup-)?(\d+\.\d+\.\d+)\.msi$/i);
      if (match) {
        // GitHub release assets replace '&' with '.' in uploaded filenames (e.g. N&T-Setup-*.msi -> N.T-Setup-*.msi)
        const ghFilename = filename.replace(/&/g, ".");
        return Response.redirect(`https://github.com/NovaKohai/N-T/releases/download/v${match[1]}/${ghFilename}`, 302);
      }
    }

    return env.ASSETS ? env.ASSETS.fetch(request) : new Response("Not found", { status: 404 });
  },
};

async function releaseDocument(url, env) {
  if (!env.ASSETS) {
    return new Response("No release has been published yet.", { status: 503 });
  }

  const response = await env.ASSETS.fetch(new Request(new URL(RELEASE_DOCUMENT, url.origin)));
  if (!response.ok) {
    return new Response("No release has been published yet.", { status: 503 });
  }

  const release = await response.json();
  for (const asset of release.assets ?? []) {
    if (asset.browser_download_url) {
      asset.browser_download_url = new URL(asset.browser_download_url, url.origin).href;
    }
  }

  return Response.json(release, { headers: { "cache-control": "public, max-age=60" } });
}
