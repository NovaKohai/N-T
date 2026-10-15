/* NeoDM media sniffing logic. Pure functions, shared by the service worker (importScripts) and the tests. */
(function (scope) {
  const MIN_SIZE = 512 * 1024;
  // YouTube itags: formats that carry audio AND video in one file (progressive/muxed) -> label.
  const MUXED = { 5: '240p', 6: '270p', 13: '176p', 17: '144p', 18: '360p', 22: '720p', 34: '360p', 35: '480p', 36: '240p', 37: '1080p', 38: '3072p', 43: '360p', 44: '480p', 45: '720p', 46: '1080p', 59: '480p', 78: '480p' };
  const AUDIO_ITAGS = new Set([139, 140, 141, 171, 172, 249, 250, 251, 256, 258, 325, 328, 338, 380, 599, 600]);
  const VIDEO_ITAGS = { 133: '240p', 134: '360p', 135: '480p', 136: '720p', 137: '1080p', 160: '144p', 242: '240p', 243: '360p', 244: '480p', 247: '720p', 248: '1080p', 264: '1440p', 266: '2160p', 271: '1440p', 278: '144p', 298: '720p', 299: '1080p', 302: '720p', 303: '1080p', 308: '1440p', 313: '2160p', 394: '144p', 395: '240p', 396: '360p', 397: '480p', 398: '720p', 399: '1080p', 400: '1440p', 401: '2160p' };
  const MANIFEST_TYPES = /mpegurl|dash|vnd\.apple/i;
  const SEGMENT_TYPES = /^video\/(mp2t|iso\.segment)|^audio\/(aac|x-aac)$/i; // HLS/DASH segments, never a whole file
  const EXT_BY_MIME = { 'video/mp4': '.mp4', 'video/webm': '.webm', 'video/x-matroska': '.mkv', 'video/quicktime': '.mov', 'video/ogg': '.ogv', 'video/x-flv': '.flv', 'video/3gpp': '.3gp', 'audio/mp4': '.m4a', 'audio/x-m4a': '.m4a', 'audio/mpeg': '.mp3', 'audio/mp3': '.mp3', 'audio/webm': '.weba', 'audio/ogg': '.ogg', 'audio/wav': '.wav', 'audio/x-wav': '.wav', 'audio/flac': '.flac', 'audio/opus': '.opus' };

  const header = (headers, name) => { const h = (headers || []).find(x => x.name && x.name.toLowerCase() === name); return h ? String(h.value || '') : ''; };
  const isHttp = value => { try { const u = new URL(value); return (u.protocol === 'http:' || u.protocol === 'https:') && !u.username && !u.password; } catch { return false; } };
  const isGoogleVideo = u => /(^|\.)googlevideo\.com$/i.test(u.hostname) && /^\/videoplayback\/?$/i.test(u.pathname);

  /** Total resource size from Content-Range (206) or Content-Length (200); 0 when unknown. */
  function totalSize(status, headers) {
    if (status === 206) { const m = /\/(\d+)\s*$/.exec(header(headers, 'content-range')); return m ? Number(m[1]) : 0; }
    const n = Number(header(headers, 'content-length')); return Number.isFinite(n) && n > 0 ? n : 0;
  }
  /** googlevideo: drop per-chunk range/rn/rbuf so every chunk of one stream maps to one URL. */
  function canonicalGoogleUrl(u) { const c = new URL(u.href); for (const p of ['range', 'rn', 'rbuf']) c.searchParams.delete(p); return c.href; }

  function describeGoogle(u) {
    const q = u.searchParams, itag = Number(q.get('itag')), mime = (q.get('mime') || '').toLowerCase();
    const clen = Number(q.get('clen')) || 0;
    let track, quality = '';
    if (mime.startsWith('audio/') || AUDIO_ITAGS.has(itag)) track = 'audio-only';
    else if (Object.prototype.hasOwnProperty.call(MUXED, itag) && mime.startsWith('video/')) { track = 'muxed'; quality = MUXED[itag]; }
    else if (mime.startsWith('video/')) { track = 'video-only'; quality = VIDEO_ITAGS[itag] || ''; }
    else return null;
    return { track, quality, mime: mime || (track === 'audio-only' ? 'audio/mp4' : 'video/mp4'), clen, itag };
  }

  /**
   * Decide whether a webRequest.onHeadersReceived details object is a downloadable media file.
   * Returns an item or null. Never returns manifests, segments or tiny responses.
   */
  function classify(details) {
    if (!details || !isHttp(details.url) || !(details.tabId >= 0)) return null;
    if (details.method && details.method !== 'GET') return null;
    if (details.statusCode !== 200 && details.statusCode !== 206) return null;
    let u; try { u = new URL(details.url); } catch { return null; }
    if (/\.(m3u8|mpd|ts|m4s)$/i.test(u.pathname)) return null;
    const type = header(details.responseHeaders, 'content-type').split(';')[0].trim().toLowerCase();
    if (!/^(video|audio)\//.test(type) || MANIFEST_TYPES.test(type) || SEGMENT_TYPES.test(type)) return null;
    if (isGoogleVideo(u)) {
      const g = describeGoogle(u); if (!g) return null;
      const size = g.clen || totalSize(details.statusCode, details.responseHeaders);
      if (size < MIN_SIZE) return null;
      const url = canonicalGoogleUrl(u);
      return { id: url, url, kind: g.track === 'audio-only' ? 'audio' : 'video', track: g.track, complete: g.track === 'muxed', size, quality: g.quality, mime: g.mime, source: 'net' };
    }
    const size = totalSize(details.statusCode, details.responseHeaders);
    if (size < MIN_SIZE) return null;
    return { id: u.href, url: u.href, kind: type.startsWith('audio/') ? 'audio' : 'video', track: 'file', complete: true, size, quality: '', mime: type, source: 'net' };
  }

  /** Item for a direct <video>/<audio> source reported by the page (size unknown). */
  function fromDom(url, tag) {
    if (!isHttp(url)) return null;
    let u; try { u = new URL(url); } catch { return null; }
    if (/\.(m3u8|mpd)$/i.test(decodeURIComponent(u.pathname))) return null;
    if (isGoogleVideo(u)) return null; // handled with proper itag knowledge by the network sniffer
    return { id: u.href, url: u.href, kind: tag === 'audio' ? 'audio' : 'video', track: 'file', complete: true, size: 0, quality: '', mime: '', source: 'dom' };
  }

  /** Insert/merge into a per-tab list. Network info (size, mime) wins over DOM info. Newest first, capped. */
  function merge(list, item, max = 30) {
    const out = list.filter(x => x.id !== item.id), old = list.find(x => x.id === item.id);
    out.unshift(old ? { ...old, ...item, size: item.size || old.size, mime: item.mime || old.mime, source: old.source === 'net' ? 'net' : item.source } : item);
    return out.slice(0, max);
  }

  const RESERVED = /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i;
  /** Page title -> safe Windows file name stem (no path chars, <=120 chars). Empty string when nothing usable. */
  function safeName(title) {
    let s = String(title || '').replace(/\s+-\s+YouTube$/i, '').normalize('NFC');
    s = s.replace(/[\u0000-\u001f\u007f\\/:*?"<>|]/g, ' ').replace(/\s+/g, ' ').trim().replace(/^[. ]+|[. ]+$/g, '');
    s = Array.from(s).slice(0, 120).join('').trim().replace(/[. ]+$/g, '');
    return RESERVED.test(s) ? s + '_' : s;
  }
  function extensionFor(item) {
    if (EXT_BY_MIME[item.mime]) return EXT_BY_MIME[item.mime];
    try { const m = /\.([a-z0-9]{2,5})$/i.exec(new URL(item.url).pathname); if (m) return '.' + m[1].toLowerCase(); } catch {}
    return item.kind === 'audio' ? '.m4a' : '.mp4';
  }
  /**
   * Suggested file name sent as optional `fileName`. For URLs with a real file name we leave the choice to the app (empty)
   * unless the stream URL has no usable name (googlevideo "videoplayback"). Track suffix prevents audio/video clobbering.
   */
  function suggestedName(item, title) {
    const stem = safeName(title); if (!stem) return '';
    let urlHasName = false;
    try { const base = new URL(item.url).pathname.split('/').pop(); urlHasName = /\.[a-z0-9]{2,5}$/i.test(base) && !/^videoplayback/i.test(base); } catch {}
    if (urlHasName && item.source === 'dom') return '';
    const suffix = item.track === 'audio-only' ? ' (audio only)' : item.track === 'video-only' ? ' (video only)' : '';
    const quality = item.quality && item.track !== 'audio-only' ? ' ' + item.quality : '';
    const ext = extensionFor(item);
    return Array.from(stem).slice(0, 255 - ext.length - suffix.length - quality.length).join('') + quality + suffix + ext;
  }

  scope.NeoSniff = { MIN_SIZE, classify, fromDom, merge, safeName, suggestedName, extensionFor, isHttp };
  if (typeof module !== 'undefined') module.exports = scope.NeoSniff;
})(globalThis);
