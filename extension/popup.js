const $ = id => document.getElementById(id);
/** One status line at the bottom of the popup. kind: 'ok' | 'err' | undefined (neutral). */
const show = (text, kind) => { const el = $('status'); el.textContent = text; el.className = kind || ''; };

function friendlyError(err) {
  if (!err) return '';
  const s = String(err);
  if (/bridge disconnected|native messaging|could not reach|not running|host not found/i.test(s)) {
    return 'N&T app is not running. Please open N&T on your computer.';
  }
  if (/timed out/i.test(s)) return 'N&T did not respond in time.';
  return s;
}

const PAUSE_KEY = 'neodm.pause';
let host = '';

async function renderPause() {
  const p = (await chrome.storage.session.get(PAUSE_KEY))[PAUSE_KEY] || {};
  const hosts = Array.isArray(p.hosts) ? p.hosts : [];
  const siteOff = !!host && hosts.includes(host), allOff = p.all === true;
  $('pauseAll').checked = allOff; $('pauseSite').checked = siteOff || allOff; $('pauseSite').disabled = allOff || !host;
  $('pauseSiteLabel').textContent = host ? `Pause N&T on ${host} for this session` : 'Pause N&T on this site for this session';
  $('resume').hidden = !(siteOff || allOff);
  $('pauseState').textContent = allOff ? 'N&T is paused on all sites until the browser closes.' : siteOff ? `N&T is paused on ${host} until the browser closes.` : 'N&T is active.';
}
const describe = NeoSniff.describeItem;
let currentMediaFilter = 'all';
let activeTab = null;

async function renderMedia(tab) {
  if (tab) activeTab = tab;
  else tab = activeTab;
  const list = $('mediaList'); list.textContent = '';
  let items = [];
  try { items = (await chrome.runtime.sendMessage({ action: 'popup-media', tabId: tab?.id }))?.items || []; } catch {}
  const tracks = items.filter(i => !i.info && NeoSniff.offeredQuality(i.quality));
  const baseItems = tracks.length > 0 ? [...tracks, ...items.filter(i => i.info)] : items;

  const filterBar = $('mediaFilterBar');
  if (filterBar) {
    const videoCount = baseItems.filter(i => !i.info && (i.kind === 'video' || /^\d+p$/i.test(i.quality) || i.mime?.startsWith('video/') || /video|mp4|webm|mkv/i.test(i.url))).length;
    const audioCount = baseItems.filter(i => !i.info && (i.kind === 'audio' || /audio/i.test(i.quality) || i.mime?.startsWith('audio/') || /m4a|mp3|aac|flac/i.test(i.url))).length;
    const totalDownloadable = baseItems.filter(i => !i.info).length;
    const shouldShow = totalDownloadable > 1 && (videoCount > 0 && audioCount > 0);
    filterBar.hidden = !shouldShow;
    if (shouldShow) {
      const allBtn = filterBar.querySelector('[data-filter="all"]');
      const vBtn = filterBar.querySelector('[data-filter="video"]');
      const aBtn = filterBar.querySelector('[data-filter="audio"]');
      if (allBtn) allBtn.textContent = `All (${totalDownloadable})`;
      if (vBtn) vBtn.textContent = `Video (${videoCount})`;
      if (aBtn) aBtn.textContent = `Audio (${audioCount})`;
    }
  }

  let shown = baseItems;
  if (currentMediaFilter === 'video') {
    shown = baseItems.filter(i => i.info || i.kind === 'video' || /^\d+p$/i.test(i.quality) || i.mime?.startsWith('video/') || /video|mp4|webm|mkv/i.test(i.url));
  } else if (currentMediaFilter === 'audio') {
    shown = baseItems.filter(i => i.info || i.kind === 'audio' || /audio/i.test(i.quality) || i.mime?.startsWith('audio/') || /m4a|mp3|aac|flac/i.test(i.url));
  }

  const dlAll = $('downloadAllBtn');
  if (dlAll) {
    const downloadable = shown.filter(i => !i.info);
    dlAll.hidden = downloadable.length < 2;
    dlAll.onclick = async () => {
      dlAll.disabled = true;
      dlAll.textContent = 'Sending…';
      show(`Sending ${downloadable.length} downloads to N&T…`);
      let sent = 0;
      for (const item of downloadable) {
        try {
          const r = await chrome.runtime.sendMessage({ action: 'popup-download', tab: { id: tab?.id, url: tab?.url, title: tab?.title }, id: item.id });
          if (r?.ok === true) sent++;
        } catch {}
      }
      if (sent > 0) {
        dlAll.textContent = 'Sent all ✓';
        dlAll.classList.add('btn-success');
        show(`Sent ${sent} of ${downloadable.length} downloads to N&T.`, 'ok');
        setTimeout(() => {
          dlAll.textContent = 'Download all';
          dlAll.classList.remove('btn-success');
          dlAll.disabled = false;
        }, 1800);
      } else {
        dlAll.textContent = 'Download all';
        show(`Could not send downloads to N&T.`, 'err');
        dlAll.disabled = false;
      }
    };
  }

  $('mediaNone').hidden = shown.length > 0;
  for (const i of shown) {
    const li = document.createElement('li'), what = document.createElement('span'); what.className = 'what';
    if (i.info) { what.textContent = i.text; what.classList.add('note'); li.append(what); list.append(li); continue; }
    const title = NeoSniff.cleanTitle(i.title || tab?.title || '');
    const meta = describe(i);
    const qInfo = NeoSniff.qualityBadgeInfo ? NeoSniff.qualityBadgeInfo(i) : null;
    const badge = document.createElement('span');
    badge.className = `q-badge ${qInfo ? qInfo.cls : 'q-def'}`;
    badge.textContent = qInfo ? qInfo.label : (i.quality || 'MEDIA');

    const isYt = /(^|\.)youtube\.com/i.test(i.url) || /(^|\.)youtu\.be/i.test(i.url);
    let selectedQuality = '';

    if (title) {
      const hdr = document.createElement('div'); hdr.className = 'item-header';
      hdr.append(badge);
      const t = document.createElement('div'); t.className = 'item-title'; t.textContent = title;
      hdr.append(t);
      const s = document.createElement('div'); s.className = 'item-meta'; s.textContent = meta;
      what.append(hdr, s);
    } else {
      const hdr = document.createElement('div'); hdr.className = 'item-header';
      hdr.append(badge);
      const s = document.createElement('span'); s.textContent = meta;
      hdr.append(s);
      what.append(hdr);
    }

    if (isYt) {
      const sel = document.createElement('select'); sel.className = 'q-select';
      sel.setAttribute('aria-label', 'Select video quality');
      const opts = [
        { val: '', label: '1080p Full HD (Best)' },
        { val: 'quality=720p', label: '720p HD' },
        { val: 'quality=480p', label: '480p SD' },
        { val: 'quality=audio', label: 'Audio only (M4A)' }
      ];
      for (const o of opts) {
        const opt = document.createElement('option'); opt.value = o.val; opt.textContent = o.label;
        sel.append(opt);
      }
      sel.onchange = () => { selectedQuality = sel.value; };
      what.append(sel);
    }

    what.title = (title ? title + '\n' : '') + i.url;
    const actions = document.createElement('div');
    actions.className = 'item-actions';

    const copyBtn = document.createElement('span');
    copyBtn.className = 'copy-action';
    copyBtn.setAttribute('role', 'button');
    copyBtn.setAttribute('tabindex', '0');
    copyBtn.title = 'Copy direct stream link to clipboard';
    copyBtn.setAttribute('aria-label', 'Copy stream link');
    copyBtn.innerHTML = '<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="13" height="13" rx="2" ry="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>';

    const copyUrl = async (e) => {
      e?.stopPropagation?.();
      let targetUrl = (i.isHls && i.rawUrl) ? i.rawUrl : i.url;
      if (selectedQuality) {
        const qStr = String(selectedQuality).trim();
        if (qStr) targetUrl = (targetUrl.split('#')[0]) + (qStr.startsWith('#') ? qStr : '#' + qStr);
      }
      try {
        await navigator.clipboard.writeText(targetUrl);
        copyBtn.classList.add('copied');
        copyBtn.title = 'Copied to clipboard!';
        copyBtn.innerHTML = '<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg>';
        show('Link copied to clipboard ✓', 'ok');
        setTimeout(() => {
          copyBtn.classList.remove('copied');
          copyBtn.title = 'Copy direct stream link to clipboard';
          copyBtn.innerHTML = '<svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="13" height="13" rx="2" ry="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>';
        }, 1500);
      } catch {
        show('Could not copy link.', 'err');
      }
    };
    copyBtn.onclick = copyUrl;
    copyBtn.onkeydown = (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); copyUrl(e); } };

    const b = document.createElement('button'); b.type = 'button'; b.textContent = 'Download';
    b.setAttribute('aria-label', `Download ${title || meta} with N&T`);
    b.onclick = async () => {
      b.disabled = true;
      b.textContent = 'Sending…';
      show('Sending to N&T…');
      const r = await chrome.runtime.sendMessage({ action: 'popup-download', tab: { id: tab?.id, url: tab?.url, title: tab?.title }, id: i.id, quality: selectedQuality }).catch(() => null);
      if (r?.ok === true) {
        b.textContent = 'Sent ✓';
        b.classList.add('btn-success');
        show('Accepted by N&T ✓', 'ok');
        setTimeout(() => {
          b.textContent = 'Download';
          b.classList.remove('btn-success');
          b.disabled = false;
        }, 1800);
      } else {
        b.textContent = 'Download';
        show(friendlyError(r?.error) || 'N&T did not accept this media.', 'err');
        b.disabled = false;
      }
    };
    actions.append(copyBtn, b);
    li.append(what, actions); list.append(li);
  }

  // Keyboard navigation across media items (ArrowUp / ArrowDown)
  list.onkeydown = (e) => {
    const buttons = Array.from(list.querySelectorAll('button:not(:disabled)'));
    const idx = buttons.indexOf(document.activeElement);
    if (e.key === 'ArrowDown' && idx < buttons.length - 1) {
      e.preventDefault();
      buttons[idx + 1].focus();
    } else if (e.key === 'ArrowUp' && idx > 0) {
      e.preventDefault();
      buttons[idx - 1].focus();
    }
  };
}
async function setPause(scope, on) {
  await chrome.runtime.sendMessage({ action: 'pause-set', scope, host, on });
  await renderPause();
}

async function checkConnection() {
  const ind = $('connectionStatus'), lbl = $('connectionLabel'), button = $('test');
  if (ind) ind.className = 'status-indicator checking';
  if (lbl) lbl.textContent = 'Testing…';
  if (button) button.disabled = true;
  show('Checking the connection to N&T…');
  try {
    const r = await chrome.runtime.sendMessage({ action: 'test' });
    if (r?.ok) {
      if (ind) ind.className = 'status-indicator online';
      if (lbl) lbl.textContent = 'Connected';
      show('Connected to N&T.', 'ok');
    } else {
      if (ind) ind.className = 'status-indicator offline';
      if (lbl) lbl.textContent = 'Offline';
      show(friendlyError(r?.error) || 'N&T is not running. Please open N&T on your computer.', 'err');
    }
  } catch {
    if (ind) ind.className = 'status-indicator offline';
    if (lbl) lbl.textContent = 'Offline';
    show('N&T is not running. Please open N&T on your computer.', 'err');
  }
  if (button) button.disabled = false;
}

chrome.storage.local.get(['captureEnabled', 'cookiesEnabled', 'ribbonEnabled', 'mediaTypes', 'status']).then(async s => {
  $('capture').checked = s.captureEnabled !== false; // on by default
  $('cookies').checked = !!s.cookiesEnabled;
  $('ribbon').checked = s.ribbonEnabled !== false;
  const types = NeoSniff.withDefaults(s.mediaTypes);
  for (const box of document.querySelectorAll('#types input[data-type]')) box.checked = !!types[box.dataset.type];
  show(s.status || 'Ready');
  try { const [tab] = await chrome.tabs.query({ active: true, currentWindow: true }); host = NeoSniff.hostOf(tab?.url); if (Number.isInteger(tab?.id)) await renderMedia(tab); } catch {}
  await renderPause();
  await renderBlacklist();

  // Subtle connection check on startup
  chrome.runtime.sendMessage({ action: 'test' }).then(r => {
    const ind = $('connectionStatus'), lbl = $('connectionLabel');
    if (r?.ok) {
      if (ind) ind.className = 'status-indicator online';
      if (lbl) lbl.textContent = 'Connected';
    } else {
      if (ind) ind.className = 'status-indicator offline';
      if (lbl) lbl.textContent = 'Offline';
    }
  }).catch(() => {
    const ind = $('connectionStatus'), lbl = $('connectionLabel');
    if (ind) ind.className = 'status-indicator offline';
    if (lbl) lbl.textContent = 'Offline';
  });
});

if ($('connectionStatus')) $('connectionStatus').onclick = checkConnection;
if ($('test')) $('test').onclick = checkConnection;

document.querySelectorAll('#mediaFilterBar .filter-chip').forEach(btn => {
  btn.onclick = () => {
    document.querySelectorAll('#mediaFilterBar .filter-chip').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
    currentMediaFilter = btn.dataset.filter || 'all';
    renderMedia();
  };
});

$('ribbon').onchange = async () => {
  await chrome.storage.local.set({ ribbonEnabled: $('ribbon').checked });
  show($('ribbon').checked ? 'Media ribbon on.' : 'Media ribbon off.');
};

for (const box of document.querySelectorAll('#types input[data-type]')) {
  box.onchange = async () => {
    const stored = (await chrome.storage.local.get('mediaTypes')).mediaTypes;
    const types = NeoSniff.withDefaults(stored); types[box.dataset.type] = box.checked;
    await chrome.storage.local.set({ mediaTypes: types });
    show(`${box.parentElement.textContent.trim()} ${box.checked ? 'on' : 'off'}.`);
  };
}

$('pauseSite').onchange = () => setPause('site', $('pauseSite').checked);
$('pauseAll').onchange = () => setPause('all', $('pauseAll').checked);
$('resume').onclick = async () => { await setPause('all', false); await setPause('site', false); show('N&T re-enabled.', 'ok'); };

for (const [id, key, name] of [['capture', 'captureEnabled', 'Download capture'], ['cookies', 'cookiesEnabled', 'Authentication cookies']]) {
  $(id).onchange = async () => {
    let enabled = $(id).checked, refused = false;
    if (enabled && id === 'cookies') {
      try { enabled = await chrome.permissions.request({ permissions: ['cookies'] }); } catch { enabled = false; }
      refused = !enabled;
    }
    $(id).checked = enabled;
    await chrome.storage.local.set({ [key]: enabled });
    if (!enabled && id === 'cookies') { try { await chrome.permissions.remove({ permissions: ['cookies'] }); } catch {} }
    if (refused) show('Cookies stay off: the browser permission was not granted.', 'err');
    else show(`${name} ${enabled ? 'on' : 'off'}.`);
  };
}

async function updateSiteBadge(domains) {
  const badge = $('siteBadge');
  if (!badge) return;
  if (!host) { badge.hidden = true; return; }
  badge.hidden = false;
  const domainEl = $('siteDomain');
  if (domainEl) domainEl.textContent = host;
  const isBlocked = Array.isArray(domains) && domains.includes(host);
  const btn = $('siteBadgeToggle');
  if (btn) {
    btn.textContent = isBlocked ? 'Unblock site' : 'Block site';
    btn.className = isBlocked ? 'badge-btn blocked' : 'badge-btn';
    btn.onclick = async () => {
      btn.disabled = true;
      if (isBlocked) {
        const r = await chrome.runtime.sendMessage({ action: 'blacklist-remove', domain: host }).catch(() => null);
        await renderBlacklist(r?.domains);
        show(`${host} is no longer blocked.`);
      } else {
        const r = await chrome.runtime.sendMessage({ action: 'blacklist-add', domain: host }).catch(() => null);
        await renderBlacklist(r?.domains);
        show(`${host} is blocked.`, 'ok');
      }
      btn.disabled = false;
    };
  }
}

async function renderBlacklist(domains) {
  const list = $('blacklistList');
  if (!list) return;
  if (!Array.isArray(domains)) {
    const r = await chrome.runtime.sendMessage({ action: 'blacklist-get' }).catch(() => null);
    domains = Array.isArray(r?.domains) ? r.domains : [];
  }
  await updateSiteBadge(domains);
  list.textContent = '';
  for (const d of domains) {
    const li = document.createElement('li');
    const span = document.createElement('span'); span.textContent = d; span.title = d;
    const btn = document.createElement('button');
    btn.type = 'button'; btn.textContent = 'Remove'; btn.setAttribute('aria-label', `Unblock ${d}`);
    btn.onclick = async () => {
      btn.disabled = true;
      const r = await chrome.runtime.sendMessage({ action: 'blacklist-remove', domain: d }).catch(() => null);
      await renderBlacklist(r?.domains);
      show(`${d} is no longer blocked.`);
    };
    li.append(span, btn);
    list.append(li);
  }
}

async function addBlocked() {
  const input = $('blacklistInput'), val = input.value.trim();
  if (!val) { input.focus(); return; }
  const r = await chrome.runtime.sendMessage({ action: 'blacklist-add', domain: val }).catch(() => null);
  if (r?.ok === false) { show(r.error || 'That site could not be blocked.', 'err'); input.focus(); return; }
  input.value = '';
  await renderBlacklist(r?.domains);
  show(r?.domain ? `${r.domain} is blocked.` : 'Site blocked.', 'ok');
}
// The Block button submits the form, so Enter in the field works too.
if ($('blacklistForm')) $('blacklistForm').onsubmit = e => { e.preventDefault(); addBlocked(); };
