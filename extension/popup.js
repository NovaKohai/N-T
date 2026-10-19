const $ = id => document.getElementById(id);
const show = text => $('status').textContent = text;
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
    b.onclick = async () => {
      b.disabled = true;
      const r = await chrome.runtime.sendMessage({ action: 'popup-download', tab: { id: tab.id, url: tab.url, title: tab.title }, id: i.id }).catch(() => null);
      show(r?.ok === true ? 'Accepted by N&T.' : r?.error || 'N&T did not accept this media.'); b.disabled = false;
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
});

$('test').onclick = async () => {
  try {
    const r = await chrome.runtime.sendMessage({ action: 'test' });
    show(r?.ok ? 'Host connected.' : r?.error || 'Host did not respond.');
  } catch { show('Could not connect to host.'); }
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
$('resume').onclick = async () => { await setPause('all', false); await setPause('site', false); show('N&T re-enabled.'); };

for (const [id, key] of [['capture', 'captureEnabled'], ['cookies', 'cookiesEnabled']]) {
  $(id).onchange = async () => {
    let enabled = $(id).checked;
    if (enabled && id === 'cookies') {
      try { enabled = await chrome.permissions.request({ permissions: ['cookies'] }); } catch { enabled = false; }
    }
    $(id).checked = enabled;
    await chrome.storage.local.set({ [key]: enabled });
    if (!enabled && id === 'cookies') await chrome.permissions.remove({ permissions: ['cookies'] });
    show(enabled ? 'Enabled.' : 'Disabled.');
  };
}
