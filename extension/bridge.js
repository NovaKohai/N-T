/* Original NDM bridge; no third-party browser integration code. */
(function (scope) {
  const http = value => { try { const u = new URL(value); return ['http:', 'https:'].includes(u.protocol) && !u.username && !u.password; } catch { return false; } };
  const ack = reply => reply?.ok === true && typeof reply.id === 'string' && reply.id.trim().length > 0;
  async function transfer(item, api, send, knownGet) {
    if (!knownGet || !http(item.url) || !http(item.finalUrl || item.url) || item.state !== 'in_progress' || item.paused || item.byExtensionId || item.incognito) return { skipped: true };
    let paused = false;
    try {
      await api.pause(item.id); paused = true;
      const current = (await api.search({ id: item.id }))[0];
      if (!current || current.state !== 'in_progress' || !current.paused) throw new Error('Browser download is no longer paused');
      const reply = await send({ url: item.finalUrl || item.url, fileName: '', referer: http(item.referrer) ? item.referrer : '', method: 'GET' });
      if (!ack(reply)) throw new Error('No durable acceptance acknowledgement');
      await api.cancel(item.id); // Never erase/removeFile: ACK precedes cancel.
      return reply;
    } catch {
      if (paused) { try { await api.resume(item.id); } catch { return { ok: false, error: 'Browser resume failed; resume it manually in chrome://downloads.' }; } }
      return { ok: false, error: 'Transfer failed; original browser download retained.' };
    }
  }
  scope.NDMBridge = { http, ack, transfer };
  if (typeof module !== 'undefined') module.exports = scope.NDMBridge;
})(globalThis);
