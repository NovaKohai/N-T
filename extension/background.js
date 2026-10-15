importScripts('bridge.js', 'sniff.js');
const { http, ack, transfer } = NDMBridge;
const { classify, fromDom, merge, suggestedName } = NeoSniff;
const methods = new Map();
let observations = new Map();
// The desktop app shows an options dialog and replies only after the user decides (up to 5 minutes).
const PING_TIMEOUT_MS = 12000, DOWNLOAD_TIMEOUT_MS = 330000;
async function status(text) { await chrome.storage.local.set({ status: text }); }
function native(request, timeoutMs = request?.action === 'ping' ? PING_TIMEOUT_MS : DOWNLOAD_TIMEOUT_MS) {
  return new Promise((resolve, reject) => {
    const port = chrome.runtime.connectNative('com.ndm.bridge');
    let done = false;
    const finish = (error, reply) => { if (done) return; done = true; clearTimeout(timer); port.disconnect(); error ? reject(error) : resolve(reply); };
    const timer = setTimeout(() => finish(new Error('Bridge timed out')), timeoutMs);
    port.onMessage.addListener(reply => finish(null, reply));
    port.onDisconnect.addListener(() => finish(new Error(chrome.runtime.lastError?.message || 'Bridge disconnected')));
    try { port.postMessage(request); } catch (e) { finish(e); }
  });
}
async function send(request, tab) {
  if (!http(request.url) || (request.method && request.method !== 'GET')) throw new Error('Only direct HTTP(S) GET URLs are supported');
  const settings = await chrome.storage.local.get(['cookiesEnabled']);
  request.cookie = '';
  if (settings.cookiesEnabled && await chrome.permissions.contains({ permissions: ['cookies'], origins: [new URL(request.url).origin + '/*'] })) {
    // Never log cookies. Preserve browser cookie-store selection when available.
    const stores = await chrome.cookies.getAllCookieStores();
    const store = stores.find(s => s.tabIds.includes(tab?.id));
    const cookies = await chrome.cookies.getAll({ url: request.url, ...(store ? { storeId: store.id } : {}) });
    request.cookie = cookies.map(c => `${c.name}=${c.value}`).join('; ');
  }
  const reply = await native(request);
  if (reply?.ok === false && typeof reply.error === 'string') throw new Error(reply.error); // e.g. user cancelled in the app dialog
  if (!ack(reply)) throw new Error('Application did not durably accept the download');
  return reply;
}

/* ---- per-tab media list: survives MV3 worker restarts through chrome.storage.session ---- */
const tabCache = new Map(), tabQueues = new Map();
const key = tabId => 'neodm.media.' + tabId;
const pageKey = url => { try { const u = new URL(url); u.hash = ''; return u.href; } catch { return ''; } };
async function loadTab(tabId) {
  if (tabCache.has(tabId)) return tabCache.get(tabId);
  const stored = (await chrome.storage.session.get(key(tabId)))[key(tabId)];
  const state = stored && Array.isArray(stored.items) ? stored : { page: '', items: [] };
  tabCache.set(tabId, state); return state;
}
/** Serialize all mutations per tab. fn(state) mutates state and returns true when the list changed. */
function mutateTab(tabId, fn) {
  const next = (tabQueues.get(tabId) || Promise.resolve()).then(async () => {
    const state = await loadTab(tabId), changed = fn(state);
    if (changed) { await chrome.storage.session.set({ [key(tabId)]: state }); await badge(tabId, state); notifyTab(tabId, state); }
    return state;
  }).catch(() => undefined);
  tabQueues.set(tabId, next); return next;
}
async function badge(tabId, state) {
  try { await chrome.action.setBadgeBackgroundColor({ tabId, color: '#2da57d' }); await chrome.action.setBadgeText({ tabId, text: state.items.length ? String(state.items.length) : '' }); } catch {}
}
function notifyTab(tabId, state) { try { chrome.tabs.sendMessage(tabId, { type: 'neodm-media', items: state.items }, () => void chrome.runtime.lastError); } catch {} }
function addItem(tabId, item) {
  return mutateTab(tabId, state => {
    const old = JSON.stringify(state.items.find(i => i.id === item.id));
    state.items = merge(state.items, item);
    return old !== JSON.stringify(state.items.find(i => i.id === item.id));
  });
}
chrome.webRequest.onHeadersReceived.addListener(details => {
  const item = classify(details);
  if (item) addItem(details.tabId, item);
}, { urls: ['http://*/*', 'https://*/*'], types: ['media', 'xmlhttprequest', 'other', 'object'] }, ['responseHeaders']);
chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (!change.url) return;
  const page = pageKey(change.url); // also fires for SPA (pushState) navigation, e.g. the next YouTube video
  mutateTab(tabId, state => {
    if (state.page === page) return false;
    const first = !state.page; state.page = page;
    if (!first) state.items = []; // first sighting after a worker restart must not wipe items already sniffed
    return true;
  });
});
chrome.tabs.onRemoved.addListener(tabId => { tabCache.delete(tabId); tabQueues.delete(tabId); chrome.storage.session.remove(key(tabId)); });

chrome.runtime.onInstalled.addListener(async () => {
  await chrome.contextMenus.removeAll();
  chrome.contextMenus.create({ id: 'link', title: 'Download link with NeoDM', contexts: ['link'] });
  chrome.contextMenus.create({ id: 'media', title: 'Download media with NeoDM', contexts: ['video', 'audio', 'image'] });
});
chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  try {
    const url = info.menuItemId === 'link' ? info.linkUrl : info.srcUrl;
    if (!http(url)) throw new Error('Not a direct HTTP(S) URL. Blob, data and protected streams are unsupported.');
    await send({ url, fileName: '', referer: http(info.pageUrl) ? info.pageUrl : '', method: 'GET' }, tab);
    await status('Accepted by NeoDM');
  } catch (e) { await status(e.message); }
});
chrome.webRequest.onBeforeRequest.addListener(details => {
  // Keep the initial method across redirects. Unknown requests after worker restart are not captured.
  const method = methods.get(details.requestId) || details.method;
  methods.set(details.requestId, method);
  const previous = observations.get(details.url);
  // A POST on the same URL makes correlation ambiguous; retain its veto for 60s.
  observations.set(details.url, { method: previous?.method !== 'GET' && previous && Date.now() - previous.time < 60000 ? previous.method : method, time: Date.now() });
  if (observations.size > 500) observations.delete(observations.keys().next().value);
}, { urls: ['http://*/*', 'https://*/*'] });
const forget = details => methods.delete(details.requestId);
chrome.webRequest.onCompleted.addListener(forget, { urls: ['http://*/*', 'https://*/*'] });
chrome.webRequest.onErrorOccurred.addListener(forget, { urls: ['http://*/*', 'https://*/*'] });
chrome.downloads.onCreated.addListener(async item => {
  const settings = await chrome.storage.local.get('captureEnabled');
  if (!settings.captureEnabled) return;
  const observed = observations.get(item.url), final = observations.get(item.finalUrl || item.url);
  const fresh = o => o?.method === 'GET' && Date.now() - o.time < 60000;
  // transfer(): pause -> native request (waits for the user's decision in the app) -> ACK -> cancel.
  // Any error/cancel reply resumes the browser download, so declining in the app never loses it.
  const result = await transfer(item, chrome.downloads, req => send(req), fresh(observed) && fresh(final));
  if (!result.skipped) await status(result.ok ? 'Accepted by NeoDM; browser download cancelled' : result.error);
});

async function downloadItem(tab, id) {
  const state = await loadTab(tab.id), item = state.items.find(i => i.id === id);
  if (!item) return { ok: false, error: 'This media is no longer available on the page.' };
  try { return await send({ url: item.url, fileName: suggestedName(item, tab.title), referer: http(tab.url) ? tab.url : '', method: 'GET' }, tab); }
  catch (e) { return { ok: false, error: e?.message || 'NeoDM did not acknowledge this URL.' }; }
}
chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (sender.id !== chrome.runtime.id || !message) return false;
  const popup = sender.url?.split(/[?#]/)[0] === chrome.runtime.getURL('popup.html');
  const page = Number.isInteger(sender.tab?.id) && sender.frameId === 0 && http(sender.url); // content script in a top frame
  if (page && message.action === 'media-list') { loadTab(sender.tab.id).then(s => respond({ items: s.items })); return true; }
  if (page && message.action === 'media-dom') {
    const found = (Array.isArray(message.sources) ? message.sources.slice(0, 50) : []).map(s => fromDom(String(s?.url), s?.tag === 'audio' ? 'audio' : 'video')).filter(Boolean);
    Promise.all(found.map(i => addItem(sender.tab.id, i))).then(() => respond({ ok: true })); return true;
  }
  if (page && message.action === 'media-download') { downloadItem(sender.tab, String(message.id)).then(respond); return true; }
  if (!popup || message.action !== 'test') return false;
  native({ action: 'ping' }).then(respond, () => respond({ ok: false, error: 'Native host unavailable. Check installation.' }));
  return true;
});
