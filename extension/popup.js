const $ = id => document.getElementById(id);
/** One status line at the bottom of the popup. kind: 'ok' | 'err' | undefined (neutral). */
const show = (text, kind) => { const el = $('status'); el.textContent = text; el.className = kind || ''; };
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
async function renderMedia(tab) {
  const list = $('mediaList'); list.textContent = '';
  let items = [];
  try { items = (await chrome.runtime.sendMessage({ action: 'popup-media', tabId: tab.id }))?.items || []; } catch {}
  const tracks = items.filter(i => !i.info && NeoSniff.offeredQuality(i.quality));
  const shown = tracks.length > 0 ? [...tracks, ...items.filter(i => i.info)] : items;
  $('mediaNone').hidden = shown.length > 0;
  for (const i of shown) {
    const li = document.createElement('li'), what = document.createElement('span'); what.className = 'what';
    if (i.info) { what.textContent = i.text; what.classList.add('note'); li.append(what); list.append(li); continue; }
    const title = NeoSniff.cleanTitle(i.title || tab?.title || '');
    const meta = describe(i);
    if (title) {
      const t = document.createElement('div'); t.className = 'item-title'; t.textContent = title;
      const s = document.createElement('div'); s.className = 'item-meta'; s.textContent = meta;
      what.append(t, s);
    } else {
      what.textContent = meta;
    }
    what.title = (title ? title + '\n' : '') + i.url;
    const b = document.createElement('button'); b.type = 'button'; b.textContent = 'Download';
    b.setAttribute('aria-label', `Download ${title || meta} with N&T`);
    b.onclick = async () => {
      b.disabled = true; show('Sending to N&T…');
      const r = await chrome.runtime.sendMessage({ action: 'popup-download', tab: { id: tab.id, url: tab.url, title: tab.title }, id: i.id }).catch(() => null);
      if (r?.ok === true) show('Accepted by N&T.', 'ok'); else show(r?.error || 'N&T did not accept this media.', 'err');
      b.disabled = false;
    };
    li.append(what, b); list.append(li);
  }
}
async function setPause(scope, on) {
  await chrome.runtime.sendMessage({ action: 'pause-set', scope, host, on });
  await renderPause();
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
});

$('test').onclick = async () => {
  const button = $('test');
  button.disabled = true; show('Checking the connection to N&T…');
  try {
    const r = await chrome.runtime.sendMessage({ action: 'test' });
    if (r?.ok) show('Connected to N&T.', 'ok'); else show(r?.error || 'N&T did not respond. Is the app installed and running?', 'err');
  } catch { show('Could not reach N&T. Is the app installed and running?', 'err'); }
  button.disabled = false;
};

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

async function renderBlacklist(domains) {
  const list = $('blacklistList');
  if (!list) return;
  if (!Array.isArray(domains)) {
    const r = await chrome.runtime.sendMessage({ action: 'blacklist-get' }).catch(() => null);
    domains = Array.isArray(r?.domains) ? r.domains : [];
  }
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
