/* Original NDM bridge; no third-party browser integration code. */
(function (scope) {
  const http = value => { try { const u = new URL(value); return ['http:', 'https:'].includes(u.protocol) && !u.username && !u.password; } catch { return false; } };
  const ack = reply => reply?.ok === true && typeof reply.id === 'string' && reply.id.trim().length > 0;
  const quiet = async fn => { try { return await fn(); } catch { return undefined; } };
  /** Stops the browser's own download and leaves nothing behind: no partial file, no finished file, no history entry. */
  async function discard(item, api) {
    await quiet(() => api.cancel(item.id));
    const current = (await quiet(() => api.search({ id: item.id })))?.[0];
    if (current && current.state === 'complete' && current.exists !== false) await quiet(() => api.removeFile(item.id));
    await quiet(() => api.erase({ id: item.id }));
  }
  /**
   * Hands a browser download over to NeoDM. The browser download is cancelled and erased FIRST (before NeoDM shows its dialog),
   * so it can never run next to the NeoDM transfer. Outcomes:
   *   ack            -> { ok: true, id }                     NeoDM owns the download
   *   user cancelled -> { ok: false, cancelled: true }       discarded, by the user's choice
   *   hard failure   -> { ok: false, restarted: true, ... }  app unreachable / error: the download is restarted in the browser
   */
  async function transfer(item, api, send, knownGet) {
    if (!knownGet || !http(item.url) || !http(item.finalUrl || item.url) || item.state !== 'in_progress' || item.paused || item.byExtensionId || item.incognito) return { skipped: true };
    const url = item.finalUrl || item.url;
    await discard(item, api);
    let failure;
    try {
      const reply = await send({ url, fileName: '', referer: http(item.referrer) ? item.referrer : '', method: 'GET' });
      if (ack(reply)) return reply;
      failure = new Error('No durable acceptance acknowledgement');
    } catch (e) {
      if (e?.cancelled === true) return { ok: false, cancelled: true, error: 'Discarded: you cancelled the download in NeoDM.' };
      failure = e;
    }
    try { await api.download({ url, conflictAction: 'uniquify' }); }
    catch { return { ok: false, error: 'NeoDM could not take the download and the browser could not restart it: ' + url }; }
    return { ok: false, restarted: true, error: 'NeoDM was unavailable (' + (failure?.message || 'error') + '); the download was restarted in the browser.' };
  }
  scope.NDMBridge = { http, ack, transfer, discard };
  if (typeof module !== 'undefined') module.exports = scope.NDMBridge;
})(globalThis);
