'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), os = require('node:os'), crypto = require('node:crypto'), zlib = require('node:zlib');
const { spawnSync } = require('node:child_process');
const { http, ack, transfer } = require('../extension/bridge.js');
const { makeBackground, response, tick, json } = require('./extension-harness.cjs');
const root = path.join(__dirname, '..');
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'extension/manifest.json'), 'utf8'));
const EXT_ID = fs.readFileSync(path.join(root, 'tools/extension-id.txt'), 'utf8').trim();
const idFromDer = der => [...crypto.createHash('sha256').update(der).digest().subarray(0, 16)].map(x => String.fromCharCode(97 + (x >> 4), 97 + (x & 15))).join('');

const item = { id: 7, url: 'https://example.org/a.zip', finalUrl: 'https://example.org/a.zip', state: 'in_progress' };
function mock(events, overrides = {}) { return { pause: async () => events.push('pause'), search: async () => { events.push('search'); return [{ ...item, paused: true }]; }, cancel: async () => events.push('cancel'), resume: async () => events.push('resume'), ...overrides }; }

test('durable ACK precedes cancel and pause precedes send', async () => {
  const events = []; const result = await transfer(item, mock(events), async () => { events.push('send'); events.push('ack'); return { ok: true, id: 'saved' }; }, true);
  assert.deepEqual(events, ['pause', 'search', 'send', 'ack', 'cancel']); assert.equal(result.ok, true);
});
test('native failure resumes, never cancels', async () => {
  const events = []; const r = await transfer(item, mock(events), async () => { events.push('send'); throw Error('missing host'); }, true);
  assert.deepEqual(events, ['pause', 'search', 'send', 'resume']); assert.equal(r.ok, false);
});
test('user cancelling in the app (error reply -> thrown) resumes the browser download and never cancels it', async () => {
  const events = []; const r = await transfer(item, mock(events), async () => { events.push('send'); throw new Error('Download cancelled by user'); }, true);
  assert.deepEqual(events, ['pause', 'search', 'send', 'resume']); assert.equal(r.ok, false);
});
test('negative/malformed ACK resumes browser', async () => {
  for (const reply of [{ ok: false, error: 'disk full' }, { ok: true }, { ok: true, id: '' }, { ok: 1, id: 'x' }, null]) {
    const events = []; await transfer(item, mock(events), async () => reply, true); assert.deepEqual(events, ['pause', 'search', 'resume']);
  }
});
test('blob/data/FTP, paused, extension and unknown/POST requests untouched', async () => {
  for (const c of [{ ...item, url: 'blob:https://example.org/1' }, { ...item, url: 'data:text/plain,x' }, { ...item, finalUrl: 'ftp://x/a' }, { ...item, paused: true }, { ...item, byExtensionId: 'other' }, { ...item, state: 'complete' }]) {
    const events = []; assert.equal((await transfer(c, mock(events), async () => { throw Error('unexpected'); }, true)).skipped, true); assert.deepEqual(events, []);
  }
  const events = []; assert.equal((await transfer(item, mock(events), async () => null, false)).skipped, true); assert.deepEqual(events, []);
});
test('pause failure never sends or cancels; completed race resumes safely', async () => {
  const events = []; await transfer(item, mock(events, { pause: async () => { events.push('pause'); throw Error(); } }), async () => { events.push('send'); }, true); assert.deepEqual(events, ['pause']);
  const e = []; await transfer(item, mock(e, { search: async () => [{ ...item, state: 'complete' }] }), async () => { e.push('send'); }, true); assert.deepEqual(e, ['pause', 'resume']);
});
test('cancel failure preserves browser download and reports failure', async () => {
  const events = []; const r = await transfer(item, mock(events, { cancel: async () => { events.push('cancel'); throw Error(); } }), async () => ({ ok: true, id: 'saved' }), true);
  assert.deepEqual(events, ['pause', 'search', 'cancel', 'resume']); assert.equal(r.ok, false);
});
test('HTTP URL and durable ACK validation', () => {
  assert.equal(http('https://x/a'), true);
  for (const s of ['javascript:alert(1)', 'blob:https://x/1', 'data:x', 'file:///a', 'https://u:p@x/']) assert.equal(http(s), false);
  assert.equal(ack({ ok: true, id: 'id' }), true); assert.equal(ack({ ok: true, id: ' ' }), false);
});

test('manifest: key derives the recorded ID, icons/permissions as designed', () => {
  assert.equal(idFromDer(Buffer.from(manifest.key, 'base64')), EXT_ID); assert.match(EXT_ID, /^[a-p]{32}$/);
  assert.equal(manifest.manifest_version, 3);
  for (const s of ['16', '48', '128']) { assert.equal(manifest.icons[s], `icons/${s}.png`); assert.equal(manifest.action.default_icon[s], `icons/${s}.png`); assert.ok(fs.existsSync(path.join(root, 'extension', manifest.icons[s]))); }
  assert.ok(manifest.optional_permissions.includes('cookies')); assert.ok(!manifest.permissions.includes('cookies'));
  for (const p of ['webRequest', 'nativeMessaging', 'downloads', 'storage']) assert.ok(manifest.permissions.includes(p), p);
  assert.deepEqual(manifest.host_permissions, ['http://*/*', 'https://*/*']);
  assert.deepEqual(manifest.content_scripts[0].js, ['content.js']); assert.notEqual(manifest.content_scripts[0].all_frames, true);
});
test('extension folder holds only runtime files (no backup, README, pem)', () => {
  const walk = (d, b = '') => fs.readdirSync(d, { withFileTypes: true }).flatMap(e => e.isDirectory() ? walk(path.join(d, e.name), b + e.name + '/') : [b + e.name]);
  assert.deepEqual(walk(path.join(root, 'extension')).sort(), ['background.js', 'bridge.js', 'content.js', 'icons/128.png', 'icons/16.png', 'icons/48.png', 'manifest.json', 'popup.css', 'popup.html', 'popup.js', 'sniff.js']);
});
test('no forbidden product names anywhere in sources, extension, installer, docs, scripts or tests', () => {
  // Built from fragments so this file does not contain the names itself.
  const banned = new RegExp(['\bI' + 'DM\b', 'Internet Download ' + 'Manager', 'q' + 'bit'].join('|'), 'i');
  const skip = ['node_modules', 'bin', 'obj', 'artifacts', 'keys', 'dist', '.git', 'payload'];
  const walk = d => fs.readdirSync(d, { withFileTypes: true }).flatMap(e => e.isDirectory() ? (skip.includes(e.name) ? [] : walk(path.join(d, e.name))) : [path.join(d, e.name)]);
  const files = walk(root).filter(f => !/\.(png|ico|crx|dll|exe|pdb|pem|msi|zip)$/i.test(f));
  assert.ok(files.length > 50, 'scan found the repository files');
  for (const f of files) assert.doesNotMatch(fs.readFileSync(f, 'utf8'), banned, f);
});
test('extension manifest version equals the product version in Directory.Build.props and CHANGELOG has it', () => {
  const props = fs.readFileSync(path.join(root, 'Directory.Build.props'), 'utf8');
  const v = /<Version>(\d+\.\d+\.\d+)<\/Version>/.exec(props)[1];
  assert.equal(JSON.parse(fs.readFileSync(path.join(root, 'extension/manifest.json'), 'utf8')).version, v);
  assert.ok(fs.readFileSync(path.join(root, 'CHANGELOG.md'), 'utf8').includes('## [' + v + '] - '), 'CHANGELOG has the current version');
});

/* ---- CRX integrity: independent parser, does not reuse tools/crx3.cjs ---- */
function readVarint(buf, pos) { let v = 0, s = 0; for (;;) { const b = buf[pos++]; v += (b & 127) * 2 ** s; if (!(b & 128)) break; s += 7; } return [v, pos]; }
function proto(buf) { const out = []; let p = 0; while (p < buf.length) { let tag, len; [tag, p] = readVarint(buf, p); [len, p] = readVarint(buf, p); assert.equal(tag & 7, 2); out.push({ num: tag >> 3, data: buf.subarray(p, p + len) }); p += len; } return out; }
function unzip(zip) {
  const eocd = zip.lastIndexOf(Buffer.from([0x50, 0x4b, 5, 6])), count = zip.readUInt16LE(eocd + 10); let p = zip.readUInt32LE(eocd + 16); const files = {};
  for (let i = 0; i < count; i++) {
    assert.equal(zip.readUInt32LE(p), 0x02014b50); const method = zip.readUInt16LE(p + 10), crc = zip.readUInt32LE(p + 16), csize = zip.readUInt32LE(p + 20), nlen = zip.readUInt16LE(p + 28), xlen = zip.readUInt16LE(p + 30), clen = zip.readUInt16LE(p + 32), off = zip.readUInt32LE(p + 42);
    const name = zip.toString('utf8', p + 46, p + 46 + nlen), lnlen = zip.readUInt16LE(off + 26), lxlen = zip.readUInt16LE(off + 28), start = off + 30 + lnlen + lxlen, raw = zip.subarray(start, start + csize);
    const data = method === 8 ? zlib.inflateRawSync(raw) : raw; assert.equal(zlib.crc32(data), crc, 'crc ' + name); files[name] = data; p += 46 + nlen + xlen + clen;
  }
  return files;
}
const crxPath = path.join(root, 'dist/NeoDM.crx');
const signingInput = (signedData, zip) => { const len = Buffer.alloc(4); len.writeUInt32LE(signedData.length); return Buffer.concat([Buffer.from('CRX3 SignedData\0', 'latin1'), len, signedData, zip]); };
test('CRX3: header id == manifest-key id == recorded id, RSA-SHA256 signature verifies', () => {
  const crx = fs.readFileSync(crxPath); assert.equal(crx.toString('latin1', 0, 4), 'Cr24'); assert.equal(crx.readUInt32LE(4), 3);
  const hlen = crx.readUInt32LE(8), header = proto(crx.subarray(12, 12 + hlen)), zip = crx.subarray(12 + hlen);
  const proofs = header.filter(f => f.num === 2), signed = header.filter(f => f.num === 10000);
  assert.equal(proofs.length, 1); assert.equal(signed.length, 1); assert.equal(header.filter(f => f.num === 3).length, 0, 'no ecdsa proof expected');
  const [pub, sig] = proto(proofs[0].data); assert.equal(pub.num, 1); assert.equal(sig.num, 2);
  const signedData = signed[0].data, crxId = proto(signedData).find(f => f.num === 1).data; assert.equal(crxId.length, 16);
  assert.equal(idFromDer(pub.data), EXT_ID);
  assert.equal([...crxId].map(x => String.fromCharCode(97 + (x >> 4), 97 + (x & 15))).join(''), EXT_ID, 'CRX header crx_id');
  assert.equal(pub.data.equals(Buffer.from(manifest.key, 'base64')), true, 'CRX signing key must be the manifest key');
  const key = crypto.createPublicKey({ key: pub.data, format: 'der', type: 'spki' });
  assert.equal(crypto.verify('sha256', signingInput(signedData, zip), key, sig.data), true, 'signature');
  const tampered = Buffer.from(zip); tampered[40] ^= 1;
  assert.equal(crypto.verify('sha256', signingInput(signedData, tampered), key, sig.data), false, 'tampering detected');
});
test('CRX3 contents are exactly the runtime files, byte-identical to extension/, no key material', () => {
  const crx = fs.readFileSync(crxPath), files = unzip(crx.subarray(12 + crx.readUInt32LE(8)));
  assert.deepEqual(Object.keys(files).sort(), ['background.js', 'bridge.js', 'content.js', 'icons/128.png', 'icons/16.png', 'icons/48.png', 'manifest.json', 'popup.css', 'popup.html', 'popup.js', 'sniff.js']);
  for (const [n, d] of Object.entries(files)) assert.equal(d.equals(fs.readFileSync(path.join(root, 'extension', n))), true, n + ' differs from extension/ (stale CRX: rerun node tools/pack-extension.cjs)');
  for (const d of Object.values(files)) assert.doesNotMatch(d.toString('latin1'), /PRIVATE KEY/);
});
test('CRX is reproducible and matches the pem (when the key is present on this machine)', { skip: !fs.existsSync(path.join(root, 'keys/neodm-extension.pem')) }, () => {
  const pem = fs.readFileSync(path.join(root, 'keys/neodm-extension.pem'));
  assert.equal(idFromDer(crypto.createPublicKey(crypto.createPrivateKey(pem)).export({ type: 'spki', format: 'der' })), EXT_ID);
  const { buildZip, buildCrx } = require('../tools/crx3.cjs'), dir = path.join(root, 'extension');
  const walk = (d, b = '') => fs.readdirSync(d, { withFileTypes: true }).flatMap(e => e.isDirectory() ? walk(path.join(d, e.name), b + e.name + '/') : [b + e.name]);
  const rebuilt = buildCrx(buildZip(walk(dir).map(name => ({ name, data: fs.readFileSync(path.join(dir, name)) }))), pem);
  assert.equal(rebuilt.equals(fs.readFileSync(crxPath)), true);
});
test('key hygiene: pem is git-ignored, absent from extension/ and tools/, docs mention backup', () => {
  assert.match(fs.readFileSync(path.join(root, '.gitignore'), 'utf8'), /^keys\/$/m);
  const walk = d => fs.readdirSync(d, { withFileTypes: true }).flatMap(e => e.isDirectory() ? walk(path.join(d, e.name)) : [path.join(d, e.name)]);
  assert.deepEqual([...walk(path.join(root, 'extension')), ...walk(path.join(root, 'tools'))].filter(f => /\.pem$/i.test(f)), []);
  assert.match(fs.readFileSync(path.join(root, 'docs/CHROME.md'), 'utf8'), /back (it )?up/i);
});

/* ---- service worker: native timeouts, capture, menus ---- */
test('native timeouts: download requests wait ~5.5 min for the app dialog, ping keeps 12 s', async () => {
  const b = makeBackground();
  await b.message({ action: 'test' }); assert.deepEqual(b.timers.map(t => t.ms), [12000]);
  b.timers.length = 0;
  b.hooks.headers(response('https://files.example/clip.mp4', 'video/mp4', 5e6)); await tick();
  const r = await b.message({ action: 'media-download', id: 'https://files.example/clip.mp4' }, b.pageSender()); assert.equal(r.ok, true);
  assert.deepEqual(b.timers.map(t => t.ms), [330000]);
});
test('app-side cancel/decline surfaces as an error (browser download must then continue)', async () => {
  const b = makeBackground({ nativeReply: () => ({ ok: false, error: 'Download cancelled by user' }) });
  b.hooks.headers(response('https://files.example/clip.mp4', 'video/mp4', 5e6)); await tick();
  const r = await b.message({ action: 'media-download', id: 'https://files.example/clip.mp4' }, b.pageSender());
  assert.equal(r.ok, false); assert.match(r.error, /cancelled/);
});
test('capture: slow app decision keeps browser download paused until ACK; cancel reply resumes it', async () => {
  for (const [reply, expected] of [[{ ok: true, id: 'x' }, ['pause', 'search', 'cancel']], [{ ok: false, error: 'cancelled' }, ['pause', 'search', 'resume']]]) {
    const b = makeBackground({ settings: { captureEnabled: true }, nativeReply: () => new Promise(r => setImmediate(() => r(reply))) });
    const events = []; b.chrome.downloads.pause = async () => events.push('pause'); b.chrome.downloads.resume = async () => events.push('resume'); b.chrome.downloads.cancel = async () => events.push('cancel');
    b.chrome.downloads.search = async () => { events.push('search'); return [{ ...item, paused: true }]; };
    b.hooks.request({ requestId: 'r', url: item.url, method: 'GET' });
    await b.hooks.download(item); assert.deepEqual(events, expected);
    assert.equal(b.nativeRequests.length, 1); assert.equal(b.nativeRequests[0].method, 'GET');
  }
});
test('service worker: menus installed; non-HTTP context fails without native calls; ping is not an enqueue; POST vetoes capture', async () => {
  const b = makeBackground({ settings: { captureEnabled: true } });
  await b.hooks.install(); assert.equal(b.nativeRequests.length, 2);
  await b.hooks.menu({ menuItemId: 'link', linkUrl: 'blob:https://x/1' }, {}); assert.equal(b.nativeRequests.length, 2); assert.match(b.local.status, /Not a direct/);
  const r = await b.message({ action: 'test' }); assert.equal(r.ok, true); assert.deepEqual(b.nativeRequests.at(-1), { action: 'ping' });
  assert.equal(b.hooks.message({ action: 'test' }, { id: 'ext-id', url: 'chrome-extension://ext-id/popup.html.evil' }, () => {}), false);
  assert.equal(b.hooks.message({ action: 'media-download', id: 'x' }, { id: 'evil', url: 'https://a.example/', frameId: 0, tab: { id: 1 } }, () => {}), false);
  assert.equal(b.hooks.message({ action: 'media-download', id: 'x' }, { id: 'ext-id', url: 'https://a.example/', frameId: 3, tab: { id: 1 } }, () => {}), false, 'sub-frames are not trusted');
  const count = b.nativeRequests.length; b.hooks.request({ requestId: 'p', url: item.url, method: 'POST' }); b.hooks.request({ requestId: 'g', url: item.url, method: 'GET' });
  await b.hooks.download(item); assert.equal(b.nativeRequests.length, count);
});

/* ---- real host binary framing (skipped when not built) ---- */
const publishedHost = path.join(root, 'dist/host/NDM.NativeHost.exe');
const hostSource = process.env.NDM_HOST_EXE || (fs.existsSync(publishedHost) ? publishedHost : path.join(root, 'src/NDM.NativeHost/bin/Debug/net8.0/NDM.NativeHost.exe'));
test('real host stdin/stdout framing: ping, multiple messages, wrong origin, 64KiB cap, invalid URL', { skip: !fs.existsSync(hostSource) }, () => {
  const scratch = fs.mkdtempSync(path.join(process.env.TMPDIR || os.tmpdir(), 'neodm-host-test-'));
  try {
    fs.cpSync(path.dirname(hostSource), scratch, { recursive: true });
    fs.writeFileSync(path.join(scratch, 'bridge-config.json'), JSON.stringify({ extensionId: EXT_ID, appPath: 'C:\\nonexistent\\NeoDM-test.exe' }));
    const exe = path.join(scratch, path.basename(hostSource));
    const frame = r => { const p = Buffer.from(JSON.stringify(r)); const h = Buffer.alloc(4); h.writeUInt32LE(p.length); return Buffer.concat([h, p]); };
    const run = (input, origin = `chrome-extension://${EXT_ID}/`) => spawnSync(exe, [origin], { input, timeout: 20000 });
    const replies = b => { const out = []; while (b.length) { assert.ok(b.length >= 4); const n = b.readUInt32LE(0); assert.ok(b.length >= n + 4); out.push(JSON.parse(b.subarray(4, n + 4))); b = b.subarray(n + 4); } return out; };
    let r = run(Buffer.concat([frame({ action: 'ping' }), frame({ action: 'ping' })])); assert.equal(r.status, 0); assert.equal(r.stderr.length, 0); assert.deepEqual(replies(r.stdout), [{ ok: true, host: 'com.ndm.bridge' }, { ok: true, host: 'com.ndm.bridge' }]);
    r = run(frame({ action: 'ping' }), 'chrome-extension://ippfbemopdifjfkpgmialgbkgkodfbbf/'); assert.equal(r.status, 2, 'the old ID must be rejected'); assert.equal(r.stdout.length, 0);
    const oversized = Buffer.alloc(4); oversized.writeUInt32LE(65537); r = run(oversized); assert.equal(r.status, 3); assert.equal(replies(r.stdout)[0].ok, false);
    for (const request of [{ url: 'blob:https://x/1' }, { url: 'https://x/a', method: 'POST' }, { url: 'https://x/a', fileName: '../x' }, { url: 'https://u:p@x/a' }]) { r = run(frame(request)); assert.equal(r.status, 0); assert.equal(replies(r.stdout)[0].ok, false); }
    r = spawnSync(exe, ['--self-test'], { timeout: 20000 }); assert.equal(r.status, 0); assert.equal(r.stdout.length, 0); assert.match(r.stderr.toString(), /PASS framing/);
  } finally { fs.rmSync(scratch, { recursive: true, force: true }); }
});
