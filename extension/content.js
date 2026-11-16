/* NeoDM media ribbon. Renders the tab's media list (kept by the service worker) in a closed shadow root. */
(() => {
  if (globalThis.__ntRibbon || globalThis.__neodmRibbon) return;
  // Shared helpers; sniff.js is injected first (manifest content_scripts) and is also used by the popup and worker.
  const { estimateBytes, fmtSize, offeredQuality, qualityFromHeight } = NeoSniff;
  const MAX_DOM = 50, SCAN_MS = 400;
  let items = [], dismissedFor = '', enabled = true, host = null, root = null, timer = null, lastDom = '';
  let currentVideos = [], resizeObs = null;
  const sending = new Set(), states = new Map(); // id -> {text, ok}
  const customOffsets = new Map(); // videoIdx -> { dx, dy } or { fixed: true, left, top }
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

  function getAllVideos() {
    try {
      const main = document.querySelector('.html5-main-video') || document.querySelector('#movie_player video') || document.querySelector('video.video-stream');
      const list = Array.from(document.querySelectorAll('video')).slice(0, MAX_DOM);
      if (main && list.includes(main)) {
        list.splice(list.indexOf(main), 1);
        list.unshift(main);
      }
      return list;
    } catch {
      return Array.from(document.querySelectorAll('video'));
    }
  }

  function findVideoElement(item, videos = getAllVideos()) {
    try {
      if (!videos.length) return null;
      if (item?.url || item?.rawUrl) {
        for (const v of videos) {
          if (v.currentSrc === item.url || v.src === item.url || v.currentSrc === item.rawUrl || v.src === item.rawUrl) return v;
          for (const s of v.querySelectorAll('source')) {
            if (s.src === item.url || s.src === item.rawUrl) return v;
          }
        }
      }
      if (videos.length === 1) return videos[0];
      const playing = videos.find(v => !v.paused && !v.ended && v.readyState > 1);
      if (playing) return playing;
      const winW = window.innerWidth || document.documentElement?.clientWidth || 0;
      const winH = window.innerHeight || document.documentElement?.clientHeight || 0;
      const inViewport = videos.find(v => {
        try {
          const r = v.getBoundingClientRect();
          return r.width > 50 && r.height > 50 && r.bottom > 50 && r.top < winH - 50 && r.right > 50 && r.left < winW - 50;
        } catch { return false; }
      });
      if (inViewport) return inViewport;
      return videos.reduce((best, v) => ((v.videoHeight * v.videoWidth) > ((best?.videoHeight || 0) * (best?.videoWidth || 0)) ? v : best), null);
    } catch {
      return null;
    }
  }

  function groupItemsByVideo(itemList) {
    const videos = getAllVideos();
    if (!videos.length) return [{ video: null, items: [...itemList] }];
    if (videos.length === 1) return [{ video: videos[0], items: [...itemList] }];
    const map = new Map(), unassigned = [];
    for (const i of itemList) {
      const v = findVideoElement(i, videos);
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
      if (groups.length > 0) groups[0].items.push(...unassigned);
      else groups.push({ video: videos[0], items: unassigned });
    }
    return groups;
  }

  function resolveQuality(i, video) {
    if (i?.quality && i.quality !== 'HLS') return i.quality;
    const v = video || findVideoElement(i);
    if (v && v.videoHeight > 0) return qualityFromHeight(v.videoHeight);
    return '';
  }

  // Row policy: standard video resolutions (480p up to 4K/8K) are offered; groups without these keep their rows.
  function offeredRows(items, video) {
    const inRange = items.filter(i => offeredQuality(resolveQuality(i, video)));
    return inRange.length > 0 ? inRange : items;
  }

  /** Display title: shared site-suffix cleanup plus surrounding quotes. */
  const cleanTitle = raw => NeoSniff.cleanTitle(raw).replace(/^["'«“\s]+|["'»”\s]+$/g, '').trim();

  function resolveTitle(i, video) {
    if (i?.title && String(i.title).trim()) return cleanTitle(i.title);

    const v = video || (i ? findVideoElement(i) : null);
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

    const h1 = document.querySelector('h1');
    if (h1 && h1.textContent.trim()) return cleanTitle(h1.textContent);

    if (document.title && document.title.trim()) {
      const cleaned = cleanTitle(document.title);
      if (cleaned) return cleaned;
    }

    if (i?.url) {
      try {
        let pathname = new URL(i.url).pathname;
        let filename = decodeURIComponent(pathname.split('/').pop() || '');
        filename = filename.replace(/\.[a-z0-9]{2,5}$/i, '');
        if (filename && filename.length > 2 && !/^videoplayback|master|playlist|index|manifest|chunk|seg/i.test(filename)) {
          return filename.replace(/[-_+]/g, ' ').trim();
        }
      } catch {}
    }

    return '';
  }

  function estimateSizeFromVideo(i, video) {
    if (i?.size && i.size > 0) return i.size;
    if (i?.estimatedSize && i.estimatedSize > 0) return i.estimatedSize;
    const v = video || findVideoElement(i);
    const dur = (v && Number.isFinite(v.duration) && v.duration > 0) ? v.duration : (i?.duration || 0);
    if (dur <= 0) return 0;
    const h = (v && v.videoHeight) || (i?.height) || (i?.quality ? parseInt(i.quality, 10) : 0) || 720;
    return estimateBytes(dur, h, i?.kind === 'audio' || i?.track === 'audio-only');
  }

  const label = (i, video) => NeoSniff.describeItem(i, resolveQuality(i, video), estimateSizeFromVideo(i, video));

  function updatePositions() {
    if (!root || !host) return;
    const bars = root.querySelectorAll('.bar');
    for (const bar of bars) {
      const idx = parseInt(bar.dataset.videoIdx, 10);
      const video = currentVideos[idx];
      const custom = customOffsets.get(idx);

      if (!video || !video.isConnected) {
        bar.className = 'bar docked';
        if (custom && custom.fixed) {
          bar.style.top = custom.top + 'px';
          bar.style.left = custom.left + 'px';
          bar.style.bottom = '';
          bar.style.right = '';
        } else {
          bar.style.top = '';
          bar.style.bottom = '16px';
          bar.style.right = '16px';
          bar.style.left = '';
        }
        bar.style.display = 'block';
        continue;
      }

      let rect = null;
      try { rect = video.getBoundingClientRect(); } catch {}
      if (!rect || (rect.width === 0 && rect.height === 0)) {
        bar.className = 'bar docked';
        if (custom && custom.fixed) {
          bar.style.top = custom.top + 'px';
          bar.style.left = custom.left + 'px';
          bar.style.bottom = '';
          bar.style.right = '';
        } else {
          bar.style.top = '';
          bar.style.bottom = '16px';
          bar.style.right = '16px';
          bar.style.left = '';
        }
        bar.style.display = 'block';
        continue;
      }

      const winW = window.innerWidth || document.documentElement?.clientWidth || 0;
      const winH = window.innerHeight || document.documentElement?.clientHeight || 0;

      if (rect.bottom < 40 || rect.top > winH - 40 || rect.right < 40 || rect.left > winW - 40) {
        bar.style.display = 'none';
        continue;
      }

      bar.style.display = 'block';
      bar.className = 'bar on-video';

      const barW = bar.offsetWidth || 268;
      const barH = bar.offsetHeight || 38;

      if (custom && typeof custom.dx === 'number' && typeof custom.dy === 'number') {
        const targetLeft = Math.max(8, Math.min(winW - barW - 8, rect.left + custom.dx));
        const targetTop = Math.max(8, Math.min(winH - barH - 8, rect.top + custom.dy));
        bar.style.left = targetLeft + 'px';
        bar.style.top = targetTop + 'px';
        bar.style.right = '';
        bar.style.bottom = '';
      } else {
        const top = Math.max(8, Math.min(winH - 60, rect.top + 8));
        const right = Math.max(8, Math.min(winW - 60, (winW - rect.right) + 8));
        bar.style.top = top + 'px';
        bar.style.right = right + 'px';
        bar.style.bottom = '';
        bar.style.left = '';
      }
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
.bar{font:12px/1.3 system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;color:#f4f7fa;background:rgba(15,23,42,0.92);backdrop-filter:blur(16px);-webkit-backdrop-filter:blur(16px);border:1px solid rgba(255,255,255,0.12);border-radius:8px;box-shadow:0 8px 24px -4px rgba(0,0,0,0.5),0 2px 6px rgba(0,0,0,0.25);width:268px;max-width:calc(100vw - 16px);overflow:hidden;pointer-events:auto;animation:ndm-in .2s cubic-bezier(.16,1,.3,1);transition:opacity .2s cubic-bezier(.16,1,.3,1),transform .2s cubic-bezier(.16,1,.3,1),box-shadow .2s ease;}
@keyframes ndm-in{from{opacity:0;transform:translateY(-6px) scale(0.98)}to{opacity:1;transform:translateY(0) scale(1)}}
@media (prefers-reduced-motion:reduce){.bar{animation:none}.bar,.min,.x,.act-btn,.dl{transition:none}}
.bar.dragging{box-shadow:0 16px 36px -4px rgba(0,0,0,0.65),0 4px 12px rgba(0,0,0,0.35);transform:scale(1.01);transition:none;}
.bar.docked{position:absolute;right:16px;bottom:16px;}
.bar.on-video{position:absolute;}
.bar.collapsed{width:auto;min-width:120px}
.bar.collapsed .rows{display:none}
.head{display:flex;align-items:center;justify-content:space-between;gap:6px;padding:5px 8px;background:rgba(255,255,255,0.03);border-bottom:1px solid rgba(255,255,255,0.08);font-weight:600;font-size:11px;letter-spacing:.5px;color:#39b995;user-select:none;cursor:grab;touch-action:none;}
.bar.collapsed .head{border-bottom:none}
.brand{display:flex;align-items:center;gap:5px;flex:1;min-width:0}
.brand span{color:#39b995;font-weight:700;letter-spacing:0.5px;font-size:11px}
.ico{display:inline-block;vertical-align:middle;color:#39b995;flex-shrink:0}
.grip{display:inline-block;color:#64748b;flex-shrink:0;opacity:.75;margin-right:1px;}
.actions{display:flex;align-items:center;gap:3px;flex-shrink:0}
.min,.x,.act-btn{all:unset;cursor:pointer;display:inline-flex;align-items:center;justify-content:center;width:26px;height:26px;border-radius:5px;font-size:13px;line-height:1;color:#94a3b8;transition:background .15s,color .15s}
.min:hover,.min:focus-visible,.x:hover,.x:focus-visible,.act-btn:hover,.act-btn:focus-visible{background:rgba(255,255,255,0.1);color:#ffffff}
.min:focus-visible,.x:focus-visible,.act-btn:focus-visible,.dl:focus-visible{outline:2px solid #39b995;outline-offset:2px}
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
.s{font-size:10px;line-height:1.2;opacity:.85;margin-top:1px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;color:#cbd5e1}
.s.err{color:#f87171;opacity:1}
.s.ok{color:#34d399;opacity:1}
.dl{all:unset;cursor:pointer;display:inline-flex;align-items:center;justify-content:center;white-space:nowrap;background:#2da57d;color:#ffffff;font-weight:600;font-size:10.5px;padding:3px 9px;min-height:26px;border-radius:4px;box-shadow:0 1px 2px rgba(0,0,0,0.25),inset 0 1px 0 rgba(255,255,255,0.15);transition:background .15s,transform .1s,box-shadow .15s;flex-shrink:0}
.dl:hover:not([disabled]),.dl:focus-visible:not([disabled]){background:#34bd90;box-shadow:0 2px 8px rgba(45,165,125,0.35);transform:translateY(-1px)}
.dl:active:not([disabled]){transform:translateY(0)}
.dl[disabled]{opacity:.55;cursor:default}
.sub .dl,.sub-dl{background:#334155;box-shadow:none}
.sub .dl:hover:not([disabled]),.sub-dl:hover:not([disabled]){background:#475569}
.sub-divider{display:flex;align-items:center;gap:6px;padding:6px 8px 3px;font-size:10px;font-weight:700;letter-spacing:.05em;text-transform:uppercase;color:#94a3b8;border-top:1px solid rgba(255,255,255,0.08);background:rgba(255,255,255,0.015)}
.sub-divider svg{color:#39b995}
.sub-row{background:rgba(255,255,255,0.02)}
.q-badge{display:inline-flex;align-items:center;justify-content:center;font-size:9px;font-weight:700;line-height:1;padding:2px 5px;border-radius:3px;letter-spacing:0.3px;text-transform:uppercase;margin-bottom:3px;flex-shrink:0;}
.q-4k{background:rgba(245,158,11,0.22);color:#fbbf24;border:1px solid rgba(245,158,11,0.35);}
.q-2k{background:rgba(99,102,241,0.22);color:#a5b4fc;border:1px solid rgba(99,102,241,0.35);}
.q-1080p{background:rgba(20,184,166,0.22);color:#2dd4bf;border:1px solid rgba(20,184,166,0.35);}
.q-720p{background:rgba(14,165,233,0.22);color:#38bdf8;border:1px solid rgba(14,165,233,0.35);}
.q-sd{background:rgba(148,163,184,0.22);color:#cbd5e1;border:1px solid rgba(148,163,184,0.35);}
.q-audio{background:rgba(168,85,247,0.22);color:#c084fc;border:1px solid rgba(168,85,247,0.35);}
.q-sub{background:rgba(148,163,184,0.22);color:#cbd5e1;border:1px solid rgba(148,163,184,0.35);}
.q-stream{background:rgba(249,115,22,0.22);color:#fb923c;border:1px solid rgba(249,115,22,0.35);}
.q-def{background:rgba(100,116,139,0.2);color:#94a3b8;border:1px solid rgba(255,255,255,0.12);}
.quality-select{all:unset;font-size:10px;font-weight:600;color:#f4f7fa;background:rgba(255,255,255,0.08);border:1px solid rgba(255,255,255,0.15);border-radius:3px;padding:2px 5px;margin-top:3px;cursor:pointer;display:inline-block;}
.quality-select:focus-visible{outline:2px solid #39b995;}
.quality-select option{background:#0f172a;color:#f4f7fa;}`;

    root.append(style);
    const container = document.fullscreenElement || document.documentElement;
    container.append(host);
  }

  let videoCleanups = [];
  function cleanupVideoListeners() {
    for (const fn of videoCleanups) { try { fn(); } catch {} }
    videoCleanups = [];
  }

  function disposeUi() {
    cleanupVideoListeners();
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
    cleanupVideoListeners();
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

      let isDragging = false, dragMoved = false, startX = 0, startY = 0, initialLeft = 0, initialTop = 0;
      head.addEventListener('pointerdown', (e) => {
        if (e.target.closest('button')) return;
        isDragging = true;
        dragMoved = false;
        startX = e.clientX;
        startY = e.clientY;
        const bRect = bar.getBoundingClientRect();
        initialLeft = bRect.left;
        initialTop = bRect.top;
        try { head.setPointerCapture(e.pointerId); } catch {}
        bar.classList.add('dragging');
      });
      head.addEventListener('pointermove', (e) => {
        if (!isDragging) return;
        const dx = e.clientX - startX;
        const dy = e.clientY - startY;
        if (Math.hypot(dx, dy) > 4) dragMoved = true;
        const winW = window.innerWidth || document.documentElement?.clientWidth || 0;
        const winH = window.innerHeight || document.documentElement?.clientHeight || 0;
        const barW = bar.offsetWidth || 268;
        const barH = bar.offsetHeight || 38;
        const curLeft = Math.max(8, Math.min(winW - barW - 8, initialLeft + dx));
        const curTop = Math.max(8, Math.min(winH - barH - 8, initialTop + dy));
        bar.style.left = curLeft + 'px';
        bar.style.top = curTop + 'px';
        bar.style.right = '';
        bar.style.bottom = '';
      });
      const endDrag = (e) => {
        if (!isDragging) return;
        isDragging = false;
        bar.classList.remove('dragging');
        try { head.releasePointerCapture(e.pointerId); } catch {}
        if (dragMoved) {
          const v = group.video;
          if (v && v.isConnected) {
            try {
              const vRect = v.getBoundingClientRect();
              const bRect = bar.getBoundingClientRect();
              customOffsets.set(gIdx, { dx: bRect.left - vRect.left, dy: bRect.top - vRect.top });
            } catch {}
          } else {
            const bRect = bar.getBoundingClientRect();
            customOffsets.set(gIdx, { fixed: true, left: bRect.left, top: bRect.top });
          }
        }
      };
      head.addEventListener('pointerup', endDrag);
      head.addEventListener('pointercancel', endDrag);
      head.addEventListener('dblclick', (e) => {
        if (e.target.closest('button')) return;
        customOffsets.delete(gIdx);
        updatePositions();
      });

      const brand = document.createElement('div');
      brand.className = 'brand';

      try {
        const grip = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        grip.setAttribute('class', 'grip');
        grip.setAttribute('viewBox', '0 0 10 16');
        grip.setAttribute('width', '8');
        grip.setAttribute('height', '12');
        grip.setAttribute('fill', 'currentColor');
        grip.innerHTML = '<circle cx="3" cy="3" r="1.2"/><circle cx="7" cy="3" r="1.2"/><circle cx="3" cy="8" r="1.2"/><circle cx="7" cy="8" r="1.2"/><circle cx="3" cy="13" r="1.2"/><circle cx="7" cy="13" r="1.2"/>';
        brand.append(grip);

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

      const blockBtn = document.createElement('button');
      blockBtn.className = 'act-btn block-btn';
      blockBtn.type = 'button';
      blockBtn.title = 'Block on this site';
      blockBtn.setAttribute('aria-label', 'Block on this site');
      blockBtn.innerHTML = '<svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><line x1="5.7" y1="5.7" x2="18.3" y2="18.3"/></svg>';
      blockBtn.addEventListener('click', (e) => {
        e.stopPropagation();
        const hostName = window.location.hostname;
        if (confirm(`Block N&T ribbon on ${hostName}?`)) {
          safeSendMessage({ action: 'blacklist-add', domain: hostName });
          disposeUi();
        }
      });

      const min = document.createElement('button');
      min.className = 'min';
      min.type = 'button';
      min.title = 'Minimize / Expand';
      min.setAttribute('aria-label', 'Minimize / Expand');
      const updateMinIcon = () => {
        const c = bar.classList.contains('collapsed');
        min.innerHTML = c
          ? '<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>'
          : '<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"><line x1="5" y1="12" x2="19" y2="12"/></svg>';
      };
      updateMinIcon();
      min.addEventListener('click', (e) => {
        e.stopPropagation();
        bar.classList.toggle('collapsed');
        updateMinIcon();
      });

      const x = document.createElement('button');
      x.className = 'x';
      x.type = 'button';
      x.innerHTML = '<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>';
      x.title = 'Dismiss';
      x.setAttribute('aria-label', 'Dismiss');
      x.addEventListener('click', (e) => {
        e.stopPropagation();
        dismissedFor = items.map(i => i.id).join('\n');
        render();
      });

      actions.append(blockBtn, min, x);
      head.append(brand, actions);

      head.addEventListener('click', (e) => {
        if (dragMoved || e.target.closest('button')) return;
        bar.classList.toggle('collapsed');
        updateMinIcon();
      });

      const rows = document.createElement('div');
      rows.className = 'rows';
      rows.setAttribute('aria-live', 'polite');

      const nonSubtitles = group.items.filter(i => !i.isSubtitle && i.kind !== 'subtitle');
      const subtitles = group.items.filter(i => i.isSubtitle || i.kind === 'subtitle');
      const ordered = [...offeredRows(nonSubtitles.length > 0 ? nonSubtitles : group.items, group.video)].sort((a, b) => (b.complete - a.complete));

      for (const i of ordered) {
        const row = document.createElement('div');
        row.className = 'row' + (i.complete ? '' : ' sub');
        row.dataset.id = i.id;

        const info = document.createElement('div');
        info.className = 'info';

        const itemTitle = resolveTitle(i, group.video);
        const resolvedQ = resolveQuality(i, group.video);
        const meta = label(i, group.video);
        const st = states.get(i.id);
        const t = document.createElement('div');
        t.className = 't';
        const s = document.createElement('div');
        s.className = 's' + (st ? (st.ok ? ' ok' : ' err') : '');

        const qInfo = NeoSniff.qualityBadgeInfo ? NeoSniff.qualityBadgeInfo(i, resolvedQ) : null;
        const badge = document.createElement('span');
        badge.className = `q-badge ${qInfo ? qInfo.cls : 'q-def'}`;
        badge.textContent = qInfo ? qInfo.label : (resolvedQ || (i.kind === 'audio' ? 'AUDIO' : 'VIDEO'));

        if (itemTitle) {
          t.textContent = itemTitle;
          t.title = itemTitle + (i.url ? '\n' + i.url : '');
          s.textContent = st ? `${meta} · ${st.text}` : (i.complete ? meta : `${meta} · not a complete video`);
        } else {
          t.textContent = meta;
          t.title = i.url;
          s.textContent = st ? st.text : (i.complete ? 'Complete file' : 'Partial stream: not a complete video');
        }

        const isYt = /(^|\.)youtube\.com/i.test(i.url) || /(^|\.)youtu\.be/i.test(i.url);
        let selectedQuality = '';
        if (isYt) {
          const sel = document.createElement('select');
          sel.className = 'quality-select';
          sel.setAttribute('aria-label', 'Select video quality');
          const opts = [
            { val: '', label: '1080p FHD (Best)' },
            { val: 'quality=720p', label: '720p HD' },
            { val: 'quality=480p', label: '480p SD' },
            { val: 'quality=audio', label: 'Audio only' }
          ];
          for (const o of opts) {
            const opt = document.createElement('option');
            opt.value = o.val;
            opt.textContent = o.label;
            sel.append(opt);
          }
          sel.onchange = () => { selectedQuality = sel.value; };
          sel.addEventListener('click', e => e.stopPropagation());
          info.append(badge, t, s, sel);
        } else {
          info.append(badge, t, s);
        }

        const b = document.createElement('button');
        b.className = 'dl';
        b.type = 'button';
        b.textContent = sending.has(i.id) ? 'Waiting for N&T\u2026' : st?.ok ? 'Sent' : 'Download with N&T';
        b.disabled = sending.has(i.id) || !!st?.ok;
        b.addEventListener('click', () => download(i, selectedQuality));

        row.append(info, b);
        rows.append(row);
      }

      if (subtitles.length > 0 && nonSubtitles.length > 0) {
        const subDivider = document.createElement('div');
        subDivider.className = 'sub-divider';
        subDivider.innerHTML = '<svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="5" width="18" height="14" rx="2"/><path d="M7 15h4M15 15h2M7 11h2M13 11h4"/></svg><span>Subtitles</span>';
        rows.append(subDivider);
        for (const sub of subtitles) {
          const row = document.createElement('div');
          row.className = 'row sub-row';
          row.dataset.id = sub.id;
          const info = document.createElement('div');
          info.className = 'info';
          const t = document.createElement('div');
          t.className = 't';
          const s = document.createElement('div');
          s.className = 's';
          const subTitle = resolveTitle(sub, group.video) || sub.title || 'Subtitles';
          t.textContent = subTitle;
          t.title = subTitle + (sub.url ? '\n' + sub.url : '');
          const ext = NeoSniff.extensionFor(sub);
          s.textContent = `${ext.replace('.', '').toUpperCase()} · ${NeoSniff.fmtSize(sub.size) || 'Ready'}`;
          info.append(t, s);
          const b = document.createElement('button');
          b.className = 'dl sub-dl';
          b.type = 'button';
          b.textContent = sending.has(sub.id) ? 'Waiting for N&T\u2026' : states.get(sub.id)?.ok ? 'Sent' : 'Download with N&T';
          b.disabled = sending.has(sub.id) || !!states.get(sub.id)?.ok;
          b.addEventListener('click', () => download(sub));
          row.append(info, b);
          rows.append(row);
        }
      }

      bar.append(head, rows);
      root.append(bar);

      if (group.video) {
        const v = group.video;
        const playerContainer = (v && typeof v.closest === 'function' ? v.closest('#movie_player, .html5-video-player, [data-testid="videoPlayer"], .video-player') : null) || v.parentElement || v;
        let fadeTimer = null;

        const setVisible = (hovered) => {
          if (fadeTimer) { clearTimeout(fadeTimer); fadeTimer = null; }
          if (hovered) {
            bar.style.opacity = '1';
          } else {
            // Soft debounce buffer before dimming during video playback to prevent flickering
            fadeTimer = setTimeout(() => {
              let isStillHovered = false;
              try {
                isStillHovered = bar.matches(':hover') || (playerContainer && playerContainer.matches(':hover')) || (v && v.matches(':hover'));
              } catch {}
              if (isStillHovered) {
                bar.style.opacity = '1';
              } else {
                bar.style.opacity = (v && !v.paused) ? '0.6' : '0.92';
              }
            }, 300);
          }
        };

        const onEnter = () => setVisible(true);
        const onLeave = () => setVisible(false);

        const attachHover = (el) => {
          if (!el) return;
          try {
            el.addEventListener('pointerenter', onEnter, { passive: true });
            el.addEventListener('pointerleave', onLeave, { passive: true });
          } catch {
            el.addEventListener('mouseenter', onEnter, { passive: true });
            el.addEventListener('mouseleave', onLeave, { passive: true });
          }
        };
        const detachHover = (el) => {
          if (!el) return;
          try {
            el.removeEventListener('pointerenter', onEnter);
            el.removeEventListener('pointerleave', onLeave);
            el.removeEventListener('mouseenter', onEnter);
            el.removeEventListener('mouseleave', onLeave);
          } catch {}
        };

        attachHover(playerContainer);
        if (playerContainer !== v) attachHover(v);
        attachHover(bar);

        const onPlay = () => setVisible(false);
        const onPause = () => setVisible(true);
        v.addEventListener('play', onPlay, { passive: true });
        v.addEventListener('pause', onPause, { passive: true });

        bar.addEventListener('focusin', onEnter);
        bar.addEventListener('focusout', onLeave);

        videoCleanups.push(() => {
          if (fadeTimer) { clearTimeout(fadeTimer); fadeTimer = null; }
          detachHover(playerContainer);
          if (playerContainer !== v) detachHover(v);
          detachHover(bar);
          try {
            v.removeEventListener('play', onPlay);
            v.removeEventListener('pause', onPause);
          } catch {}
        });

        setVisible(false);
      }
    });

    updatePositions();
  }

  async function download(i, quality) {
    if (!isAlive()) { dispose(); return; }
    if (sending.has(i.id)) return;
    sending.add(i.id); states.delete(i.id); render();
    try {
      const msg = { action: 'media-download', id: i.id };
      if (quality) msg.quality = quality;
      const r = await chrome.runtime.sendMessage(msg);
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
    delete globalThis.__ntRibbon;
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

      try {
        for (const tr of m.querySelectorAll('track[src]')) {
          const tSrc = tr.src || tr.getAttribute('src');
          if (tSrc && /^https?:/i.test(tSrc)) {
            sources.push({ url: tSrc, tag: 'track', title: tr.label || '' });
          }
        }
      } catch {}
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
      window.addEventListener('click', (e) => {
        const link = e.target && e.target.closest ? e.target.closest('a[href]') : null;
        if (!link || !link.href) return;
        if (e.altKey && !e.shiftKey && !e.ctrlKey) {
          e.preventDefault();
          e.stopPropagation();
          safeSendMessage({ action: 'force-download', url: link.href, referer: window.location.href });
        } else if (e.shiftKey && !e.altKey) {
          safeSendMessage({ action: 'bypass-download', url: link.href });
        }
      }, { capture: true, passive: false });

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
