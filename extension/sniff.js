/* NeoDM media sniffing logic. Pure functions, shared by the service worker (importScripts) and the tests. */
(function (scope) {
  const MIN_SIZE = 512 * 1024, IMAGE_MIN_SIZE = 64 * 1024;
  /** File-type groups the user can switch on/off (popup). Images exclude GIF, which is its own group. */
  const TYPE_IDS = ['video', 'audio', 'image', 'gif', 'archive', 'program', 'document', 'other'];
  const DEFAULT_TYPES = { video: true, audio: true, image: false, gif: false, archive: true, program: true, document: true, other: true };
  const EXT_TYPES = {
    video: 'mp4 mkv webm avi mov flv wmv m4v mpg mpeg 3gp ogv ts', audio: 'mp3 m4a aac flac wav ogg oga opus wma weba mid midi',
    image: 'jpg jpeg png bmp webp svg ico tif tiff avif heic heif', gif: 'gif', archive: 'zip rar 7z tar gz tgz bz2 xz zst iso cab img',
    program: 'exe msi msix appx apk dmg pkg deb rpm bat cmd jar ps1 appimage', document: 'pdf doc docx xls xlsx ppt pptx txt rtf odt ods odp csv epub mobi md json xml html htm',
  };
  const TYPE_BY_EXT = {}; for (const [type, list] of Object.entries(EXT_TYPES)) for (const e of list.split(' ')) TYPE_BY_EXT[e] = type;
  /** Type group of a download from its file name or URL (extension first), then its MIME type. Unknown = 'other'. */
  function typeOfFile(nameOrUrl, mime) {
    let path = String(nameOrUrl || '');
    try { if (/^https?:/i.test(path)) path = decodeURIComponent(new URL(path).pathname); } catch {}
    const m = /\.([a-z0-9]{1,8})$/i.exec(path.split(/[?#]/)[0]);
    if (m && TYPE_BY_EXT[m[1].toLowerCase()]) return TYPE_BY_EXT[m[1].toLowerCase()];
    const t = String(mime || '').toLowerCase();
    if (t === 'image/gif') return 'gif';
    if (t.startsWith('video/')) return 'video'; if (t.startsWith('audio/')) return 'audio'; if (t.startsWith('image/')) return 'image';
    if (/zip|rar|7z|tar|gzip/.test(t)) return 'archive'; if (/msdownload|msi|executable|x-apple-diskimage/.test(t)) return 'program';
    if (/pdf|msword|officedocument|text\//.test(t)) return 'document';
    return 'other';
  }
  /** Type group of a sniffed media-list item. */
  const typeOfItem = item => item.kind === 'gif' ? 'gif' : item.kind === 'image' ? 'image' : item.kind === 'audio' ? 'audio' : 'video';
  const withDefaults = stored => { const out = { ...DEFAULT_TYPES }; for (const id of TYPE_IDS) if (stored && typeof stored[id] === 'boolean') out[id] = stored[id]; return out; };
  /** Session pause state { all: bool, hosts: [hostname] } -> is NeoDM paused for this page/host? */
  const hostOf = url => { try { return new URL(url).hostname.toLowerCase(); } catch { return ''; } };
  const isPaused = (state, ...urls) => !!state && (state.all === true || urls.some(u => { const h = hostOf(u); return h && Array.isArray(state.hosts) && state.hosts.includes(h); }));
  // YouTube itags: formats that carry audio AND video in one file (progressive/muxed) -> label.
  const MUXED = { 5: '240p', 6: '270p', 13: '176p', 17: '144p', 18: '360p', 22: '720p', 34: '360p', 35: '480p', 36: '240p', 37: '1080p', 38: '3072p', 43: '360p', 44: '480p', 45: '720p', 46: '1080p', 59: '480p', 78: '480p' };
  const AUDIO_ITAGS = new Set([139, 140, 141, 171, 172, 249, 250, 251, 256, 258, 325, 328, 338, 380, 599, 600]);
  const VIDEO_ITAGS = { 133: '240p', 134: '360p', 135: '480p', 136: '720p', 137: '1080p', 160: '144p', 242: '240p', 243: '360p', 244: '480p', 247: '720p', 248: '1080p', 264: '1440p', 266: '2160p', 271: '1440p', 278: '144p', 298: '720p', 299: '1080p', 302: '720p', 303: '1080p', 308: '1440p', 313: '2160p', 394: '144p', 395: '240p', 396: '360p', 397: '480p', 398: '720p', 399: '1080p', 400: '1440p', 401: '2160p' };
  const MANIFEST_TYPES = /mpegurl|dash|vnd\.apple/i;
  const SEGMENT_TYPES = /^video\/(mp2t|iso\.segment)|^audio\/(aac|x-aac)$/i; // HLS/DASH segments, never a whole file
  const EXT_BY_MIME = { 'video/mp4': '.mp4', 'video/webm': '.webm', 'video/x-matroska': '.mkv', 'video/quicktime': '.mov', 'video/ogg': '.ogv', 'video/x-flv': '.flv', 'video/3gpp': '.3gp', 'audio/mp4': '.m4a', 'audio/x-m4a': '.m4a', 'audio/mpeg': '.mp3', 'audio/mp3': '.mp3', 'audio/webm': '.weba', 'audio/ogg': '.ogg', 'audio/wav': '.wav', 'audio/x-wav': '.wav', 'audio/flac': '.flac', 'audio/opus': '.opus', 'image/gif': '.gif', 'image/jpeg': '.jpg', 'image/png': '.png', 'image/webp': '.webp', 'image/avif': '.avif', 'image/svg+xml': '.svg', 'image/bmp': '.bmp' };

  const header = (headers, name) => { const h = (headers || []).find(x => x.name && x.name.toLowerCase() === name); return h ? String(h.value || '') : ''; };
  const isHttp = value => { try { const u = new URL(value); return (u.protocol === 'http:' || u.protocol === 'https:') && !u.username && !u.password; } catch { return false; } };
  const isGoogleVideo = u => /(^|\.)googlevideo\.com$/i.test(u.hostname) && /^\/videoplayback\/?$/i.test(u.pathname);

  /** Total resource size from Content-Range (206) or Content-Length (200); 0 when unknown. */
  function totalSize(status, headers) {
    if (status === 206) { const m = /\/(\d+)\s*$/.exec(header(headers, 'content-range')); return m ? Number(m[1]) : 0; }
    const n = Number(header(headers, 'content-length')); return Number.isFinite(n) && n > 0 ? n : 0;
  }
  /* ---- grouping: every piece of one resource maps to one canonical URL ---- */
  const RANGE_PARAMS = new Set('range bytestart byteend start end begin offset rn rbuf bytes from to sq seg segment fragment chunk part frag'.split(' '));
  const CACHE_PARAMS = new Set('_ _t ts cb nocache rnd random cachebust cachebuster timestamp time'.split(' '));
  const GOOGLE_PARAMS = new Set(['range', 'rn', 'rbuf', 'sq', 'ump', 'alr']); // per-request only; itag, id, expire, sig, lsig, mime, clen, dur stay
  const PATH_RANGE = /\/(?:range\/\d+-\d*|bytes[=_]\d+-\d*)(?=\/|$)/gi;
  const SEG_MAX = 16 * 1024 * 1024; // a fragment of an adaptive stream is never larger than this
  const MAX_KEYS = 200;
  const queryKeys = u => (u.search ? u.search.slice(1).split('&') : []).map(p => { let k = p.split('=')[0]; try { k = decodeURIComponent(k.replace(/\+/g, ' ')); } catch {} return k.toLowerCase(); });
  /** Query string without the named parameters; other parameters keep their exact bytes (signatures, tokens). */
  function stripQuery(u, names) {
    if (!u.search) return '';
    const parts = u.search.slice(1).split('&'), keys = queryKeys(u), keep = parts.filter((_, i) => !names.has(keys[i]));
    return keep.length === parts.length ? u.search : keep.length ? '?' + keep.join('&') : '';
  }
  const rangeLike = u => queryKeys(u).some(k => RANGE_PARAMS.has(k)) || (PATH_RANGE.lastIndex = 0, PATH_RANGE.test(u.pathname));
  /** URL without range, segment-number and cache-busting parameters or range path segments. Other parameters are kept. */
  function canonicalUrl(url) {
    let u; try { u = new URL(url); } catch { return String(url || ''); }
    const c = new URL(u.href); c.hash = '';
    if (isGoogleVideo(u)) { c.search = stripQuery(u, GOOGLE_PARAMS); return c.href; }
    c.search = stripQuery(u, new Set([...RANGE_PARAMS, ...CACHE_PARAMS]));
    const path = u.pathname.replace(PATH_RANGE, ''); if (path !== u.pathname) c.pathname = path || '/';
    return c.href;
  }
  const canonicalGoogleUrl = u => canonicalUrl(u.href);
  /** Key shared by every request for the same resource. */
  const groupKey = item => (item && (item.groupKey || canonicalUrl(item.rawUrl || item.url))) || '';
  const capKeys = (obj, max = MAX_KEYS) => { const keys = Object.keys(obj); for (let i = 0; i < keys.length - max; i++) delete obj[keys[i]]; };

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

  const QUALITY_REGEX = /(?:^|[_\-./?&=])(2160p|1440p|1080p|720p|480p|360p|240p|144p|4k|2k)(?:$|[_\-./?&=])/i;
  const RES_REGEX = /(?:^|[_\-./?&=])(\d{3,4})x(\d{3,4})(?:$|[_\-./?&=])/i;
  const QUERY_RES = /[?&](?:res|resolution|height|quality|q)=(\d{3,4}p?)(?:&|$)/i;
  const PATH_HEIGHT_REGEX = /(?:^|[_\-./])(2160|1440|1080|720|480|360|240|144)(?:p|\.[a-z0-9]{2,5}|$|[_\-./?&=])/i;

  function extractQuality(url) {
    if (!url) return '';
    let str = String(url);
    try { str = decodeURIComponent(str); } catch {}
    const m = QUALITY_REGEX.exec(str);
    if (m) {
      const q = m[1].toLowerCase();
      return q === '4k' ? '2160p' : q === '2k' ? '1440p' : q;
    }
    const qm = QUERY_RES.exec(str);
    if (qm) {
      const v = qm[1].toLowerCase();
      if (/^\d{3,4}p$/.test(v)) return v;
      const num = Number(v);
      if (num >= 2100) return '2160p';
      if (num >= 1400) return '1440p';
      if (num >= 1000) return '1080p';
      if (num >= 700) return '720p';
      if (num >= 460) return '480p';
      if (num >= 340) return '360p';
      if (num >= 220) return '240p';
      if (num >= 140) return '144p';
    }
    const rm = RES_REGEX.exec(str);
    if (rm) {
      const h = Math.min(Number(rm[1]), Number(rm[2]));
      if (h >= 2100) return '2160p';
      if (h >= 1400) return '1440p';
      if (h >= 1000) return '1080p';
      if (h >= 700) return '720p';
      if (h >= 460) return '480p';
      if (h >= 340) return '360p';
      if (h >= 220) return '240p';
      if (h >= 140) return '144p';
    }
    const pm = PATH_HEIGHT_REGEX.exec(str);
    if (pm) return pm[1] + 'p';
    return '';
  }

  function qualityFromHeight(h) {
    if (!h || h <= 0) return '';
    if (h >= 2100) return '2160p';
    if (h >= 1400) return '1440p';
    if (h >= 1000) return '1080p';
    if (h >= 700) return '720p';
    if (h >= 460) return '480p';
    if (h >= 340) return '360p';
    if (h >= 220) return '240p';
    return h + 'p';
  }

  const FRAG_NAME = /(?:^|[^a-z])(?:seg(?:ment)?|frag(?:ment)?|chunk|part)[-_ ]?\d+\.[a-z0-9]{2,5}$|^0\d{2,}\.(?:mp4|m4s|m4v|m4a|webm|aac|ts)$|[-_.]0\d{3,}\.(?:mp4|m4v|m4a|webm|ts)$|^init(?:ialization)?[-_.\d]*\.(?:mp4|m4v|m4a|webm|ts)$/i;
  const parseRange = text => { const m = /bytes\s+(\d+)-(\d+)\/(\d+|\*)/i.exec(text); return m ? { start: Number(m[1]), end: Number(m[2]), total: m[3] === '*' ? 0 : Number(m[3]) } : null; };
  /** Fragment candidate: origin + path with digit runs replaced by '#' + mime. */
  function segmentCandidate(u, type, total) {
    if (total >= SEG_MAX) return null;
    let name = u.pathname.split('/').pop(); try { name = decodeURIComponent(name); } catch {}
    return { key: u.origin + u.pathname.replace(/\d+/g, '#') + '|' + type, path: u.pathname, nameHit: FRAG_NAME.test(name) };
  }
  const isHlsUrl = url => {
    try {
      const u = new URL(url);
      return /\.m3u8($|[?#&])/i.test(u.pathname) || /[?&][^=]*=[^&]*\.m3u8($|[?#&])/i.test(u.search) || /\.m3u8/i.test(u.pathname + u.search);
    } catch { return false; }
  };
  const isHlsMime = type => /mpegurl/i.test(type);
  const isSegmentUrl = u => {
    try {
      const p = u.pathname.toLowerCase();
      if (/\.(ts|m4s|mpd)($|[?#&])/i.test(p) || p.endsWith('.ts') || p.endsWith('.m4s') || p.endsWith('.mpd')) return true;
      if (/[?&][^=]*=[^&]*\.(ts|m4s)($|[?#&])/i.test(u.search) || /\.(ts|m4s)($|[?#&])/i.test(u.search)) return true;
      let name = p.split('/').pop() || '';
      try { name = decodeURIComponent(name); } catch {}
      return FRAG_NAME.test(name);
    } catch { return false; }
  };
  const isGoogleVideoUrl = url => {
    try {
      const u = new URL(url);
      return /(^|\.)googlevideo\.com$/i.test(u.hostname) && /^\/videoplayback\/?$/i.test(u.pathname);
    } catch { return false; }
  };
  const isMediaResponse = details => {
    if (!details) return false;
    const type = header(details.responseHeaders, 'content-type').trim().toLowerCase();
    return /^(video|audio|image)\//i.test(type) || isHlsMime(type) || isHlsUrl(details.url) || isGoogleVideoUrl(details.url);
  };

  /**
   * Examine one onHeadersReceived details object. Returns { item?, piece?, seg? }:
   * item = a downloadable file, piece = a partial response whose total is unknown, seg = a possible stream fragment.
   * `state` (optional) is the per-tab tracker; it is only read here.
   */
  function inspect(details, state) {
    const out = {};
    if (!details || !isHttp(details.url) || !(details.tabId >= 0)) return out;
    if (details.method && details.method !== 'GET') return out;
    if (details.statusCode !== 200 && details.statusCode !== 206) return out;
    let u; try { u = new URL(details.url); } catch { return out; }
    const type = header(details.responseHeaders, 'content-type').split(';')[0].trim().toLowerCase();

    // HLS stream manifest (.m3u8): offered as a complete MP4 video download via FFmpeg
    if (isHlsUrl(details.url) || isHlsMime(type)) {
      const url = canonicalUrl(u.href);
      out.item = { id: url, url, rawUrl: u.href, groupKey: url, kind: 'video', track: 'stream', isHls: true, complete: true, size: 0, quality: extractQuality(u.href), mime: 'application/vnd.apple.mpegurl', source: 'net' };
      return out;
    }

    if (isSegmentUrl(u) || SEGMENT_TYPES.test(type)) {
      out.seg = segmentCandidate(u, type, totalSize(details.statusCode, details.responseHeaders));
      return out;
    }
    if (/^image\//.test(type) && !MANIFEST_TYPES.test(type)) {
      const size = totalSize(details.statusCode, details.responseHeaders);
      if (size < IMAGE_MIN_SIZE) return out;
      out.item = { id: u.href, url: u.href, kind: type === 'image/gif' ? 'gif' : 'image', track: 'file', complete: true, size, quality: '', mime: type, source: 'net' };
      return out;
    }
    if (!isGoogleVideo(u) && (!/^(video|audio)\//.test(type) || MANIFEST_TYPES.test(type))) return out;
    if (isGoogleVideo(u)) {
      const g = describeGoogle(u); if (!g) return out;
      let size = g.clen || totalSize(details.statusCode, details.responseHeaders);
      if (!g.clen) {
        const dur = Number(u.searchParams.get('dur')) || 0;
        if (dur > 0) {
          const bps = g.track === 'audio-only' ? 128000 : 2500000;
          size = Math.max(size, Math.round((dur * bps) / 8));
        }
      }
      if (size < MIN_SIZE) return out;
      const url = canonicalGoogleUrl(u);
      out.item = { id: url, url, rawUrl: u.href, groupKey: url, kind: g.track === 'audio-only' ? 'audio' : 'video', track: g.track, complete: g.track === 'muxed', size, quality: g.quality, mime: g.mime, source: 'net' };
      return out;
    }
    const partial = details.statusCode === 206, cr = partial ? parseRange(header(details.responseHeaders, 'content-range')) : null;
    const length = Number(header(details.responseHeaders, 'content-length')) || 0;
    const total = partial ? (cr ? cr.total : 0) : length;
    const url = canonicalUrl(u.href), ranged = rangeLike(u);
    out.seg = segmentCandidate(u, type, total);
    const roundChunk = !partial && length >= 65536 && length <= 8 * 1024 * 1024 && length % 65536 === 0;
    if (partial ? total < MIN_SIZE : roundChunk && (ranged || !!(state && state.pieces && state.pieces[url]))) {
      out.piece = { key: url, mime: type, len: partial && cr ? cr.end - cr.start + 1 : length };
      return out;
    }
    if (total < MIN_SIZE) return out;
    out.item = { id: url, url, rawUrl: u.href, groupKey: url, kind: type.startsWith('audio/') ? 'audio' : 'video', track: 'file', complete: true, size: total, quality: type.startsWith('audio/') ? '' : extractQuality(u.href), mime: type, source: 'net' };
    if (!partial && ranged) out.item.ambiguous = true; // a range URL piece, hidden when page streams through MSE
    return out;
  }
  /** Decide whether a webRequest.onHeadersReceived details object is a downloadable media file. Item or null. */
  const classify = details => inspect(details).item || null;

  /** Item for a direct <video>/<audio> source reported by the page (size unknown). */
  function fromDom(url, tag, duration = 0, height = 0, width = 0, pageUrl = '', title = '') {
    const isBlob = /^blob:/i.test(url);
    const effectiveUrl = (isBlob && pageUrl && isHttp(pageUrl)) ? pageUrl : url;
    if (!isHttp(effectiveUrl)) return null;
    let u; try { u = new URL(effectiveUrl); } catch { return null; }
    if (/\.mpd$/i.test(decodeURIComponent(u.pathname))) return null;
    if (isGoogleVideo(u)) return null; // handled with proper itag knowledge by the network sniffer
    const isYt = /(^|\.)youtube\.com$/i.test(u.hostname) || /(^|\.)youtu\.be$/i.test(u.hostname);
    const key = isYt ? (canonicalUrl(u.origin + u.pathname + (u.searchParams.get('v') ? '?v=' + u.searchParams.get('v') : ''))) : canonicalUrl(u.href);
    const isHls = isHlsUrl(u.href);
    let quality = tag === 'audio' ? '' : extractQuality(u.href);
    if (!quality && height > 0) quality = qualityFromHeight(height);
    if (!quality && isYt) quality = '1080p';
    let estimatedSize = 0;
    if (duration > 0) {
      const h = height || (quality ? parseInt(quality, 10) : 0) || 720;
      const bps = h >= 2100 ? 12000000 : h >= 1400 ? 6000000 : h >= 1000 ? 2800000 : h >= 700 ? 1500000 : h >= 460 ? 800000 : h >= 340 ? 450000 : tag === 'audio' ? 128000 : 1500000;
      estimatedSize = Math.round((duration * bps) / 8);
    }
    const item = { id: key, url: key, rawUrl: u.href, groupKey: key, kind: tag === 'audio' ? 'audio' : 'video', track: isHls ? 'stream' : 'file', isHls, complete: true, size: 0, quality, mime: isHls ? 'application/vnd.apple.mpegurl' : (isYt ? 'video/mp4' : ''), source: 'dom', primary: true };
    if (title) item.title = title;
    if (estimatedSize > 0) item.estimatedSize = estimatedSize;
    if (duration > 0) item.duration = duration;
    return item;
  }

  /** Insert/merge into a per-tab list. The first URL stays; network info (size, mime) wins over DOM info; the largest size is kept. Newest first, capped. */
  function merge(list, item, max = 30) {
    const out = list.filter(x => x.id !== item.id), old = list.find(x => x.id === item.id);
    if (!old) { out.unshift(item); return out.slice(0, max); }
    const m = { ...old, ...item, url: old.url, rawUrl: old.rawUrl || item.rawUrl, size: Math.max(item.size || 0, old.size || 0), mime: item.mime || old.mime, quality: item.quality || old.quality || '', source: old.source === 'net' ? 'net' : item.source };
    if (item.estimatedSize || old.estimatedSize) m.estimatedSize = Math.max(item.estimatedSize || 0, old.estimatedSize || 0);
    if (old.primary || item.primary) m.primary = true; else delete m.primary;
    if (old.ambiguous && item.ambiguous) m.ambiguous = true; else delete m.ambiguous;
    out.unshift(m); return out.slice(0, max);
  }
  const sameItem = (a, b) => { const strip = x => x && JSON.stringify({ ...x, time: 0 }); return strip(a) === strip(b); };

  /**
   * Feed one response into the per-tab state { items, pieces, segs, segmented, mse }. Returns true when the visible list changed,
   * 'quiet' when only the trackers changed, false otherwise.
   */
  function ingest(state, details, types, now = Date.now()) {
    state.pieces = state.pieces || {}; state.segs = state.segs || {}; state.segmented = state.segmented || {};
    const r = inspect(details, state);
    if (r.seg) {
      if (state.segmented[r.seg.key]) return false;
      const list = state.segs[r.seg.key] = state.segs[r.seg.key] || [];
      if (!list.includes(r.seg.path) && list.length < 3) list.push(r.seg.path);
      capKeys(state.segs);
      if (r.seg.nameHit || list.length >= 3) {
        state.segmented[r.seg.key] = true; capKeys(state.segmented); delete state.segs[r.seg.key];
        state.items = state.items.filter(i => i.segKey !== r.seg.key);
        return true;
      }
    }
    if (r.piece) { state.pieces[r.piece.key] = { mime: r.piece.mime, len: r.piece.len }; capKeys(state.pieces); return 'quiet'; }
    if (!r.item || (types && !types[typeOfItem(r.item)])) return r.seg ? 'quiet' : false;
    return upsert(state, r.item, now, r.seg && r.seg.key) || (r.seg ? 'quiet' : false);
  }
  /** Add or merge one item (network or DOM); true when anything but the time changed. */
  function upsert(state, item, now = Date.now(), segKey) {
    state.pieces = state.pieces || {}; if (state.pieces[item.groupKey]) delete state.pieces[item.groupKey];
    const next = { ...item, time: now }; if (segKey) next.segKey = segKey;
    const before = state.items.find(i => i.id === next.id);
    state.items = merge(state.items, next);
    return !sameItem(before, state.items.find(i => i.id === next.id));
  }
  /** Clear everything learned about the current page. */
  function resetTracking(state) { state.items = []; state.pieces = {}; state.segs = {}; state.segmented = {}; state.mse = false; }

  const SEGMENTED_ROW = { id: 'neodm-segmented', kind: 'info', info: true, text: 'Segmented stream, cannot be saved as one file' };
  const familyOf = i => { try { return new URL(i.url).origin; } catch { return ''; } };
  const isFile = i => i.track === 'file' && i.kind !== 'image' && i.kind !== 'gif';
  /** Items to show: primary first, then complete files by size, leftovers dropped, capped. `info` adds the segmented-stream row. */
  function listItems(state, { max = 30, info = false } = {}) {
    const hasHls = (state.items || []).some(i => i.isHls);
    const hasPrimary = (state.items || []).some(i => i.primary);
    let list = (state.items || []).filter(i => {
      if (i.primary || i.isHls) return true;
      if (hasPrimary && (i.track === 'video-only' || i.track === 'audio-only')) return false;
      // If we captured an HLS stream on this tab, hide raw segment chunks
      if (hasHls && !i.isHls && !i.primary && (i.size || 0) < 16 * 1024 * 1024) return false;
      if (state.mse && i.ambiguous && !i.primary) return false;
      return true;
    });
    const biggest = {};
    for (const i of list) if (isFile(i)) biggest[familyOf(i)] = Math.max(biggest[familyOf(i)] || 0, i.size || 0);
    list = list.filter(i => i.primary || i.isHls || !isFile(i) || !(biggest[familyOf(i)] >= MIN_SIZE && (i.size || 0) < biggest[familyOf(i)] * 0.1));
    list = list.map((i, n) => [i, n]).sort(([a, x], [b, y]) => (!!b.primary - !!a.primary) || (!!b.isHls - !!a.isHls) || (!!b.complete - !!a.complete) || ((b.size || 0) - (a.size || 0)) || (x - y)).map(([i]) => i).slice(0, max);
    return list;
  }
  /** The tab's list without the typed filter applied; used by tests and by the popup after filtering. */
  const withInfo = (state, list) => (state.segmented && Object.keys(state.segmented).length && !list.some(i => i.isHls) ? [...list, { ...SEGMENTED_ROW }] : list);

  const SITE_SUFFIX = /\s+[-–—|/•·]\s*(?:[A-Za-z0-9_-]+\.[a-z]{2,6}|YouTube|Facebook|Instagram|Twitter|X|Vimeo|Dailymotion|TikTok|Twitch|Reddit).*$/i;
  const SOCIAL_PREFIX = /\s+on\s+[A-Za-z0-9_.-]+:.*$/i;
  const RESERVED = /^(con|prn|aux|nul|com[1-9]|lpt[1-9])$/i;
  /** Page title -> safe Windows file name stem (no path chars, <=120 chars). Empty string when nothing usable. */
  function safeName(title) {
    let s = String(title || '')
      .replace(SITE_SUFFIX, '')
      .replace(SOCIAL_PREFIX, '')
      .normalize('NFC');
    s = s.replace(/[\u0000-\u001f\u007f\\/:*?"<>|]/g, ' ').replace(/\s+/g, ' ').trim().replace(/^[. ]+|[. ]+$/g, '');
    s = Array.from(s).slice(0, 120).join('').trim().replace(/[. ]+$/g, '');
    return RESERVED.test(s) ? s + '_' : s;
  }
  function extensionFor(item) {
    if (item.isHls || item.track === 'stream' || /mpegurl/i.test(item.mime || '')) return '.mp4';
    if (EXT_BY_MIME[item.mime]) return EXT_BY_MIME[item.mime];
    try { const m = /\.([a-z0-9]{2,5})$/i.exec(new URL(item.url).pathname); if (m && m[1].toLowerCase() !== 'm3u8') return '.' + m[1].toLowerCase(); } catch {}
    return item.kind === 'audio' ? '.m4a' : item.kind === 'gif' ? '.gif' : item.kind === 'image' ? '.jpg' : '.mp4';
  }
  /**
   * Suggested file name sent as optional `fileName`. For URLs with a real file name we leave the choice to the app (empty)
   * unless the stream URL has no usable name (googlevideo "videoplayback"). Track suffix prevents audio/video clobbering.
   */
  function suggestedName(item, title) {
    const stem = safeName(title); if (!stem) return '';
    let urlHasName = false;
    try { const base = new URL(item.url).pathname.split('/').pop(); urlHasName = /\.[a-z0-9]{2,5}$/i.test(base) && !/^videoplayback/i.test(base) && !/\.m3u8$/i.test(base); } catch {}
    if (urlHasName && item.source === 'dom' && !item.isHls) return '';
    const suffix = item.track === 'audio-only' ? ' (audio only)' : item.track === 'video-only' ? ' (video only)' : '';
    const quality = item.quality && item.track !== 'audio-only' && item.quality !== 'HLS' ? ' ' + item.quality : '';
    const ext = extensionFor(item);
    return Array.from(stem).slice(0, 255 - ext.length - suffix.length - quality.length).join('') + quality + suffix + ext;
  }

  scope.NeoSniff = { TYPE_IDS, DEFAULT_TYPES, typeOfFile, typeOfItem, withDefaults, hostOf, isPaused, MIN_SIZE, IMAGE_MIN_SIZE, classify, inspect, ingest, upsert, resetTracking, listItems, withInfo, isMediaResponse, canonicalUrl, groupKey, SEGMENTED_ROW, MAX_KEYS, fromDom, merge, safeName, suggestedName, extensionFor, isHttp, extractQuality, qualityFromHeight };
  if (typeof module !== 'undefined') module.exports = scope.NeoSniff;
})(globalThis);
