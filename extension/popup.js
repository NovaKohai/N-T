const $ = id => document.getElementById(id);
const show = text => $('status').textContent = text;

chrome.storage.local.get(['captureEnabled', 'cookiesEnabled', 'ribbonEnabled', 'status']).then(s => {
  $('capture').checked = !!s.captureEnabled;
  $('cookies').checked = !!s.cookiesEnabled;
  $('ribbon').checked = s.ribbonEnabled !== false;
  show(s.status || 'Ready');
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
