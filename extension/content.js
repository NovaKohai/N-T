/* NeoDM media ribbon. Renders the tab's media list (kept by the service worker) in a closed shadow root. */
(() => {
  if (globalThis.__ntRibbon || globalThis.__neodmRibbon) return;
  const MAX_DOM = 50, SCAN_MS = 400;
  let items = [], dismissedFor = '', enabled = true, host = null, root = null, timer = null, lastDom = '';
  let currentVideos = [], resizeObs = null;
  const sending = new Set(), states = new Map(); // id -> {text, ok}
  const events = ['loadedmetadata', 'loadstart', 'play'];
  let obs = null;

  function isAlive() {
    try {
      if (typeof chrome === 'undefined' || !chrome?.runtime) return false;
      if ('id' in chrome.runtime) return Boolean(chrome.runtime.id);
      return typeof chrome.runtime.sendMessage === 'function';
    } catch {
      return false;
    }
  }

  const fmtSize = n => !n ? '' : n >= 1073741824 ? (n / 1073741824).toFixed(2) + ' GB' : n >= 1048576 ? (n / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(n / 1024)) + ' KB';

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

  function getAllVideos() {
    try {
      const main = document.querySelector('.html5-main-video') || document.querySelector('#movie_player video') || document.querySelector('video.video-stream');
      const list = Array.from(document.querySelectorAll('video')).slice(0, MAX_DOM);
      if (main && list.includes(main)) {
        list.splice(list.indexOf(main), 1);
        list.unshift(main);
      }
      if (list.length >= 10) return list;
      for (const el of Array.from(document.querySelectorAll('*')).slice(0, 50)) {
        if (el.shadowRoot) {
          for (const v of el.shadowRoot.querySelectorAll('video')) {
            if (!list.includes(v)) list.push(v);
          }
        }
      }
      return list;
    } catch {
      return Array.from(document.querySelectorAll('video'));
    }
  }

  function findVideoElement(item) {
    try {
      const videos = getAllVideos();
      if (!videos.length) return null;
      if (item?.url || item?.rawUrl) {
        for (const v of videos) {
          if (v.currentSrc === item.url || v.src === item.url || v.currentSrc === item.rawUrl || v.src === item.rawUrl) return v;
          if (v.querySelector && v.querySelector(`source[src="${item.url}"], source[src="${item.rawUrl}"]`)) return v;
        }
      }
      if (videos.length === 1) return videos[0];
      const playing = videos.find(v => !v.paused && !v.ended && v.readyState > 1);
      if (playing) return playing;
      return videos.reduce((best, v) => ((v.videoHeight * v.videoWidth) > ((best?.videoHeight || 0) * (best?.videoWidth || 0)) ? v : best), null);
    } catch {
      return null;
    }
  }

  function groupItemsByVideo(itemList) {
    const videos = getAllVideos();
    if (!videos.length) {
      return [{ video: null, items: [...itemList] }];
    }
    if (videos.length === 1) {
      return [{ video: videos[0], items: [...itemList] }];
    }
    const map = new Map();
    const unassigned = [];
    for (const i of itemList) {
      const v = findVideoElement(i);
      if (v) {
        if (!map.has(v)) map.set(v, []);
        map.get(v).push(i);
      } else {
        unassigned.push(i);
      }
    }
    const groups = [];
    for (const [video, vItems] of map.entries()) {
      groups.push({ video, items: vItems });
    }
    if (unassigned.length > 0) {
      if (groups.length > 0) {
        groups[0].items.push(...unassigned);
      } else {
        groups.push({ video: videos[0], items: unassigned });
      }
    }
    return groups;
  }

  function resolveQuality(i) {
    if (i?.quality && i.quality !== 'HLS') return i.quality;
    const v = findVideoElement(i);
    if (v && v.videoHeight > 0) return qualityFromHeight(v.videoHeight);
    return '';
  }

  const SITE_SUFFIX = /\s+[-–—|/•·]\s*(?:[A-Za-z0-9_-]+\.[a-z]{2,6}|YouTube|Facebook|Instagram|Twitter|X|Vimeo|Dailymotion|TikTok|Twitch|Reddit).*$/i;
  const SOCIAL_PREFIX = /\s+on\s+[A-Za-z0-9_.-]+:.*$/i;

  function cleanTitle(raw) {
    if (!raw) return '';
    let s = String(raw).trim();
    s = s.replace(SITE_SUFFIX, '');
    s = s.replace(SOCIAL_PREFIX, '');
    s = s.replace(/^["'«“\s]+|["'»”\s]+$/g, '').trim();
    return s;
  }

  function resolveTitle(i) {
    if (i?.title && String(i.title).trim()) return cleanTitle(i.title);

    const v = findVideoElement(i);
    if (v) {
      const vt = v.getAttribute('title') || v.getAttribute('aria-label');
      if (vt && vt.trim() && !/^(?:twitch\s+)?video\s+player$|^html5\s+(?:video\s+)?player$|^media\s+player$/i.test(vt.trim())) {
        return cleanTitle(vt);
      }
    }

    try {
      const og = document.querySelector('meta[property="og:title"], meta[name="og:title"]')?.getAttribute('content');
      if (og && og.trim()) return cleanTitle(og);
      const tw = document.querySelector('meta[name="twitter:title"], meta[property="twitter:title"]')?.getAttribute('content');
      if (tw && tw.trim()) return cleanTitle(tw);
    } catch {}

    try {
      const h1 = document.querySelector('h1');
      if (h1 && h1.textContent.trim()) return cleanTitle(h1.textContent);
    } catch {}

    try {
      if (document.title && document.title.trim()) {
        const cleaned = cleanTitle(document.title);
        if (cleaned) return cleaned;
      }
    } catch {}

    try {
      let pathname = new URL(i.url).pathname;
      let filename = decodeURIComponent(pathname.split('/').pop() || '');
      filename = filename.replace(/\.[a-z0-9]{2,5}$/i, '');
      if (filename && filename.length > 2 && !/^videoplayback|master|playlist|index|manifest|chunk|seg/i.test(filename)) {
        return filename.replace(/[-_+]/g, ' ').trim();
      }
    } catch {}

    return '';
  }

  function estimateSizeFromVideo(i) {
    if (i?.size && i.size > 0) return i.size;
    if (i?.estimatedSize && i.estimatedSize > 0) return i.estimatedSize;
    const v = findVideoElement(i);
    const dur = (v && Number.isFinite(v.duration) && v.duration > 0) ? v.duration : (i?.duration || 0);
    if (dur <= 0) return 0;
    const h = (v && v.videoHeight) || (i?.height) || (i?.quality ? parseInt(i.quality, 10) : 0) || 720;
    let bps = 1500000;
    if (h >= 2100) bps = 12000000;
    else if (h >= 1400) bps = 6000000;
    else if (h >= 1000) bps = 2800000;
    else if (h >= 700) bps = 1500000;
    else if (h >= 460) bps = 800000;
    else if (h >= 340) bps = 450000;
    else if (i?.kind === 'audio' || i?.track === 'audio-only') bps = 128000;
    return Math.round((dur * bps) / 8);
  }

  function label(i) {
    const q = resolveQuality(i);
    const kind = i.kind === 'gif' ? 'GIF' : i.kind === 'image' ? 'Image' : i.track === 'audio-only' ? 'Audio only' : i.track === 'video-only' ? 'Video only (no sound)' : i.kind === 'audio' ? 'Audio' : i.isHls ? 'Video (MP4)' : 'Video';
    let sizeStr = fmtSize(i.size);
    if (!sizeStr) {
      const est = estimateSizeFromVideo(i);
      if (est > 0) sizeStr = '~' + fmtSize(est);
    }
    return [kind, q && q !== 'HLS' ? q : '', sizeStr].filter(Boolean).join(' · ');
  }

  function updatePositions() {
    if (!root || !host) return;
    const bars = root.querySelectorAll('.bar');
    for (const bar of bars) {
      const idx = parseInt(bar.dataset.videoIdx, 10);
      const video = currentVideos[idx];
      if (!video || !video.isConnected) {
        bar.className = 'bar docked';
        bar.style.top = '';
        bar.style.bottom = '16px';
        bar.style.right = '16px';
        bar.style.left = '';
        bar.style.display = 'block';
        continue;
      }

      let rect = null;
      try { rect = video.getBoundingClientRect(); } catch {}
      if (!rect || (rect.width === 0 && rect.height === 0)) {
        bar.className = 'bar docked';
        bar.style.top = '';
        bar.style.bottom = '16px';
        bar.style.right = '16px';
        bar.style.left = '';
        bar.style.display = 'block';
        continue;
      }

      const winW = window.innerWidth || document.documentElement?.clientWidth || 1024;
      const winH = window.innerHeight || document.documentElement?.clientHeight || 768;

      if (rect.bottom < 40 || rect.top > winH - 40 || rect.right < 40 || rect.left > winW - 40) {
        bar.style.display = 'none';
        continue;
      }

      bar.style.display = 'block';
      bar.className = 'bar on-video';

      const top = Math.max(8, Math.min(winH - 60, rect.top + 8));
      const right = Math.max(8, Math.min(winW - 60, (winW - rect.right) + 8));

      bar.style.top = top + 'px';
      bar.style.right = right + 'px';
      bar.style.bottom = '';
      bar.style.left = '';
    }
  }

  function onFullscreenChange() {
    if (!host) return;
    const fs = document.fullscreenElement || document.webkitFullscreenElement || document.mozFullScreenElement;
    const target = fs || document.documentElement;
    if (host.parentElement !== target) {
      try { target.append(host); } catch {}
    }
    updatePositions();
  }

  function build() {
    host = document.createElement('div');
    host.setAttribute('data-neodm-ribbon', '');
    host.setAttribute('data-nt-ribbon', '');
    host.style.cssText = 'all:initial!important;position:fixed!important;inset:0!important;width:100vw!important;height:100vh!important;pointer-events:none!important;z-index:2147483647!important;overflow:visible!important;';
    root = host.attachShadow({ mode: 'closed' });

    const style = document.createElement('style');
    style.textContent = `*,*::before,*::after{box-sizing:border-box;}
.bar{font:12px/1.3 system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;color:#f4f7fa;background:rgba(15,23,42,0.92);backdrop-filter:blur(16px);-webkit-backdrop-filter:blur(16px);border:1px solid rgba(255,255,255,0.12);border-radius:8px;box-shadow:0 8px 24px -4px rgba(0,0,0,0.5),0 2px 6px rgba(0,0,0,0.25);width:268px;max-width:calc(100vw - 16px);overflow:hidden;pointer-events:auto;animation:ndm-in .2s cubic-bezier(.16,1,.3,1);transition:opacity .25s ease,transform .2s ease,width .2s ease}
@keyframes ndm-in{from{opacity:0;transform:translateY(-6px) scale(0.98)}to{opacity:1;transform:translateY(0) scale(1)}}
.bar.docked{position:absolute;right:16px;bottom:16px;}
.bar.on-video{position:absolute;}
.bar.collapsed{width:auto;min-width:115px}
.bar.collapsed .rows{display:none}
.head{display:flex;align-items:center;justify-content:space-between;gap:6px;padding:5px 8px;background:rgba(255,255,255,0.03);border-bottom:1px solid rgba(255,255,255,0.08);font-weight:600;font-size:11px;letter-spacing:.5px;color:#39b995;user-select:none;cursor:default}
.bar.collapsed .head{border-bottom:none}
.brand{display:flex;align-items:center;gap:5px;flex:1;min-width:0}
.brand span{color:#39b995;font-weight:700;letter-spacing:0.5px;font-size:11px}
.ico{display:inline-block;vertical-align:middle;color:#39b995;flex-shrink:0}
.actions{display:flex;align-items:center;gap:3px;flex-shrink:0}
.min,.x{all:unset;cursor:pointer;display:inline-flex;align-items:center;justify-content:center;width:18px;height:18px;border-radius:4px;font-size:13px;line-height:1;color:#94a3b8;transition:background .15s,color .15s}
.min:hover,.min:focus-visible,.x:hover,.x:focus-visible{background:rgba(255,255,255,0.1);color:#ffffff}
.rows{max-height:180px;overflow-y:auto;overflow-x:hidden;overscroll-behavior:contain}
.rows::-webkit-scrollbar{width:3px}
.rows::-webkit-scrollbar-track{background:transparent}
.rows::-webkit-scrollbar-thumb{background:rgba(255,255,255,0.2);border-radius:3px}
.rows::-webkit-scrollbar-thumb:hover{background:rgba(255,255,255,0.35)}
.row{display:flex;align-items:center;gap:8px;padding:5px 8px;transition:background .15s}
.row:hover{background:rgba(255,255,255,0.04)}
.row+.row{border-top:1px solid rgba(255,255,255,0.06)}
.info{flex:1;min-width:0}
.t{white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-weight:600;font-size:11px;line-height:1.25;color:#f4f7fa}
.s{font-size:10px;line-height:1.2;opacity:.85;margin-top:1px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;color:#94a3b8}
.s.err{color:#f87171;opacity:1}
.s.ok{color:#34d399;opacity:1}
.dl{all:unset;cursor:pointer;white-space:nowrap;background:#2da57d;color:#ffffff;font-weight:600;font-size:10.5px;padding:3px 8px;border-radius:4px;box-shadow:0 1px 2px rgba(0,0,0,0.25),inset 0 1px 0 rgba(255,255,255,0.15);transition:background .15s,transform .1s,box-shadow .15s;flex-shrink:0}
.dl:hover:not([disabled]),.dl:focus-visible:not([disabled]){background:#34bd90;box-shadow:0 2px 8px rgba(45,165,125,0.35);transform:translateY(-1px)}
.dl:active:not([disabled]){transform:translateY(0)}
.dl[disabled]{opacity:.55;cursor:default}
.sub .dl{background:#334155;box-shadow:none}
.sub .dl:hover:not([disabled]){background:#475569}`;

    root.append(style);
    const container = document.fullscreenElement || document.documentElement;
    container.append(host);
  }

  function disposeUi() {
    host?.remove();
    host = null;
    root = null;
    currentVideos = [];
    if (resizeObs) {
      try { resizeObs.disconnect(); } catch {}
      resizeObs = null;
    }
  }

  function render() {
    const visible = enabled && items.length && dismissedFor !== items.map(i => i.id).join('\n');
    if (!visible) {
      disposeUi();
      return;
    }
    if (!host) {
      build();
    } else if (!host.isConnected) {
      const container = document.fullscreenElement || document.documentElement;
      container.append(host);
    }

    // Remove existing bar elements
    for (const oldBar of Array.from(root.querySelectorAll('.bar'))) {
      oldBar.remove();
    }

    const groups = groupItemsByVideo(items);
    currentVideos = groups.map(g => g.video);

    if (typeof ResizeObserver !== 'undefined') {
      if (resizeObs) { try { resizeObs.disconnect(); } catch {} }
      resizeObs = new ResizeObserver(() => updatePositions());
      for (const v of currentVideos) {
        if (v) try { resizeObs.observe(v); } catch {}
      }
    }

    groups.forEach((group, gIdx) => {
      const bar = document.createElement('div');
      bar.className = 'bar' + (group.video ? ' on-video' : ' docked');
      bar.setAttribute('role', 'region');
      bar.setAttribute('aria-label', 'N&T media');
      bar.dataset.videoIdx = String(gIdx);

      const head = document.createElement('div');
      head.className = 'head';

      const brand = document.createElement('div');
      brand.className = 'brand';

      try {
        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('class', 'ico');
        svg.setAttribute('viewBox', '0 0 24 24');
        svg.setAttribute('width', '13');
        svg.setAttribute('height', '13');
        svg.setAttribute('fill', 'none');
        svg.setAttribute('stroke', 'currentColor');
        svg.setAttribute('stroke-width', '2.5');
        svg.setAttribute('stroke-linecap', 'round');
        svg.setAttribute('stroke-linejoin', 'round');
        const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('d', 'M12 3v12m0 0l-4-4m4 4l4-4M4 17v2a2 2 0 002 2h12a2 2 0 002-2v-2');
        svg.append(path);
        brand.append(svg);
      } catch {}

      const title = document.createElement('span');
      title.textContent = 'N&T';
      brand.append(title);

      const actions = document.createElement('div');
      actions.className = 'actions';

      const min = document.createElement('button');
      min.className = 'min';
      min.type = 'button';
      min.textContent = '\u2013';
      min.title = 'Minimize / Expand';
      min.setAttribute('aria-label', 'Minimize / Expand');
      min.addEventListener('click', (e) => {
        e.stopPropagation();
        bar.classList.toggle('collapsed');
        min.textContent = bar.classList.contains('collapsed') ? '+' : '\u2013';
      });

      const x = document.createElement('button');
      x.className = 'x';
      x.type = 'button';
      x.textContent = '\u00d7';
      x.title = 'Dismiss';
      x.setAttribute('aria-label', 'Dismiss');
      x.addEventListener('click', (e) => {
        e.stopPropagation();
        dismissedFor = items.map(i => i.id).join('\n');
        render();
      });

      actions.append(min, x);
      head.append(brand, actions);

      head.addEventListener('click', (e) => {
        if (e.target.closest('button')) return;
        bar.classList.toggle('collapsed');
        min.textContent = bar.classList.contains('collapsed') ? '+' : '\u2013';
      });

      const rows = document.createElement('div');
      rows.className = 'rows';
      rows.setAttribute('aria-live', 'polite');

      const ordered = [...group.items].sort((a, b) => (b.complete - a.complete));
      for (const i of ordered) {
        const row = document.createElement('div');
        row.className = 'row' + (i.complete ? '' : ' sub');
        row.dataset.id = i.id;

        const info = document.createElement('div');
        info.className = 'info';

        const itemTitle = resolveTitle(i);
        const meta = label(i);
        const st = states.get(i.id);
        const t = document.createElement('div');
        t.className = 't';
        const s = document.createElement('div');
        s.className = 's' + (st ? (st.ok ? ' ok' : ' err') : '');

        if (itemTitle) {
          t.textContent = itemTitle;
          t.title = itemTitle + (i.url ? '\n' + i.url : '');
          s.textContent = st ? `${meta} · ${st.text}` : (i.complete ? meta : `${meta} · not a complete video`);
        } else {
          t.textContent = meta;
          t.title = i.url;
          s.textContent = st ? st.text : (i.complete ? 'Complete file' : 'Partial stream: not a complete video');
        }
        info.append(t, s);

        const b = document.createElement('button');
        b.className = 'dl';
        b.type = 'button';
        b.textContent = sending.has(i.id) ? 'Waiting for N&T\u2026' : st?.ok ? 'Sent' : 'Download with N&T';
        b.disabled = sending.has(i.id) || !!st?.ok;
        b.addEventListener('click', () => download(i));

        row.append(info, b);
        rows.append(row);
      }

      bar.append(head, rows);
      root.append(bar);

      if (group.video) {
        const updateOpacity = () => {
          let isHovered = false;
          try {
            isHovered = bar.matches(':hover') || (group.video && group.video.matches(':hover'));
          } catch {}
          if (isHovered) {
            bar.style.opacity = '1';
          } else if (group.video && !group.video.paused) {
            bar.style.opacity = '0.35';
          } else {
            bar.style.opacity = '0.9';
          }
        };

        group.video.addEventListener('mouseenter', updateOpacity, { passive: true });
        group.video.addEventListener('mouseleave', updateOpacity, { passive: true });
        group.video.addEventListener('play', updateOpacity, { passive: true });
        group.video.addEventListener('pause', updateOpacity, { passive: true });
        bar.addEventListener('mouseenter', updateOpacity, { passive: true });
        bar.addEventListener('mouseleave', updateOpacity, { passive: true });
        updateOpacity();
      }
    });

    updatePositions();
  }

  async function download(i) {
    if (!isAlive()) { dispose(); return; }
    if (sending.has(i.id)) return;
    sending.add(i.id); states.delete(i.id); render();
    try {
      const r = await chrome.runtime.sendMessage({ action: 'media-download', id: i.id });
      const ok = r?.ok === true && typeof r.id === 'string' && r.id.trim().length > 0;
      states.set(i.id, ok ? { ok: true, text: 'Accepted by N&T' } : { ok: false, text: r?.error || 'Not accepted by N&T' });
    } catch (err) {
      if (/context invalidated/i.test(err?.message || '')) dispose();
      states.set(i.id, { ok: false, text: 'N&T bridge unavailable' });
    }
    sending.delete(i.id); render();
  }

  function setItems(next) {
    items = Array.isArray(next) ? next : [];
    if (!items.length) dismissedFor = '';
    render();
  }

  const schedule = () => {
    if (!isAlive()) { dispose(); return; }
    if (timer === null) timer = setTimeout(scanDom, SCAN_MS);
  };

  function dispose() {
    try { obs?.disconnect(); } catch {}
    if (timer !== null) { clearTimeout(timer); timer = null; }
    for (const e of events) {
      try { document.removeEventListener(e, schedule, { capture: true, passive: true }); } catch {}
    }
    try { window.removeEventListener('scroll', updatePositions, { capture: true }); } catch {}
    try { window.removeEventListener('resize', updatePositions); } catch {}
    try { document.removeEventListener('fullscreenchange', onFullscreenChange); } catch {}
    try { document.removeEventListener('webkitfullscreenchange', onFullscreenChange); } catch {}
    disposeUi();
    delete globalThis.__neodmRibbon;
  }

  function safeSendMessage(message) {
    if (!isAlive()) { dispose(); return Promise.resolve(); }
    try {
      const p = chrome.runtime.sendMessage(message);
      if (p && typeof p.catch === 'function') {
        return p.catch(err => {
          if (/context invalidated/i.test(err?.message || '')) dispose();
        });
      }
      return Promise.resolve(p);
    } catch (err) {
      if (/context invalidated/i.test(err?.message || '')) dispose();
      return Promise.resolve();
    }
  }

  // Direct <video>/<audio> sources (blob: and encrypted media are ignored; the network sniffer covers streamed media).
  function scanDom() {
    if (!isAlive()) { dispose(); return; }
    timer = null;
    const sources = [];
    const isYt = /(^|\.)youtube\.com$/i.test(window.location.hostname) || /(^|\.)youtu\.be$/i.test(window.location.hostname);
    const pageUrl = window.location.href;
    for (const m of getAllVideos().slice(0, MAX_DOM)) {
      if (m.mediaKeys) continue;
      const urls = [m.currentSrc, m.getAttribute('src') && m.src, ...Array.from(m.querySelectorAll('source[src]')).map(s => s.src)];
      const url = urls.find(u => u && /^https?:/i.test(u));
      const duration = (Number.isFinite(m.duration) && m.duration > 0) ? m.duration : 0;
      const height = m.videoHeight || 0;
      const width = m.videoWidth || 0;
      const entry = { url: url || (isYt && pageUrl ? pageUrl : 'blob:'), tag: m.tagName.toLowerCase() };
      if (isYt && pageUrl) entry.pageUrl = pageUrl;
      if (duration > 0) entry.duration = duration;
      if (height > 0) entry.height = height;
      if (width > 0) entry.width = width;
      if (isYt) {
        const docTitle = resolveTitle(null) || (document.title ? document.title.trim() : '');
        if (docTitle) entry.title = docTitle;
      }
      if (url || isYt) sources.push(entry);
      else if (/^blob:/i.test(m.currentSrc || '')) sources.push(entry); // tells the worker this page streams through MediaSource
    }
    const sig = JSON.stringify(sources);
    if (sig === lastDom) return; lastDom = sig;
    if (sources.length) safeSendMessage({ action: 'media-dom', sources });
  }

  try {
    if (isAlive()) {
      obs = new MutationObserver(schedule);
      obs.observe(document.documentElement, { subtree: true, childList: true, attributes: true, attributeFilter: ['src'] });
      for (const e of events) document.addEventListener(e, schedule, { capture: true, passive: true });
      document.addEventListener('playing', schedule, { capture: true, passive: true });
      for (const ytEvent of ['yt-navigate-finish', 'yt-player-updated', 'spfdone']) {
        try { window.addEventListener(ytEvent, schedule, { passive: true }); } catch {}
      }
      window.addEventListener('scroll', updatePositions, { passive: true, capture: true });
      window.addEventListener('resize', updatePositions, { passive: true });
      document.addEventListener('fullscreenchange', onFullscreenChange, { passive: true });
      document.addEventListener('webkitfullscreenchange', onFullscreenChange, { passive: true });

      chrome.runtime.onMessage.addListener(m => {
        if (!isAlive()) { dispose(); return; }
        if (m && (m.type === 'nt-media' || m.type === 'neodm-media')) setItems(m.items);
      });
      chrome.storage.onChanged.addListener((c, area) => {
        if (!isAlive()) { dispose(); return; }
        if (area === 'local' && c.ribbonEnabled) { enabled = c.ribbonEnabled.newValue !== false; render(); }
      });
      chrome.storage.local.get('ribbonEnabled').then(s => {
        if (!isAlive()) { dispose(); return; }
        enabled = s.ribbonEnabled !== false;
        return safeSendMessage({ action: 'media-list' });
      }).then(r => {
        if (r?.items) setItems(r.items);
      }).catch(() => {});
    }
  } catch {
    dispose();
  }
  if (isAlive()) scanDom();
  globalThis.__ntRibbon = globalThis.__neodmRibbon = { refresh: scanDom, items: () => items.slice(), dispose };
})();
