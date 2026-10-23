'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
const ffManifestPath = path.join(root, 'extension-firefox', 'manifest.json');

test('firefox manifest: gecko ID, MV3, permissions and background scripts', () => {
  assert.ok(fs.existsSync(ffManifestPath), 'extension-firefox/manifest.json must exist');
  const manifest = JSON.parse(fs.readFileSync(ffManifestPath, 'utf8'));

  assert.equal(manifest.manifest_version, 3);
  assert.equal(manifest.name, 'N&T Integration');
  assert.equal(manifest.browser_specific_settings?.gecko?.id, 'neodm@unlimitedera.com');
  assert.equal(manifest.browser_specific_settings?.gecko?.strict_min_version, '109.0');

  for (const s of ['16', '48', '128']) {
    assert.equal(manifest.icons[s], `icons/${s}.png`);
    assert.equal(manifest.action.default_icon[s], `icons/${s}.png`);
  }

  assert.ok(manifest.permissions.includes('nativeMessaging'));
  assert.ok(manifest.permissions.includes('downloads'));
  assert.ok(manifest.permissions.includes('contextMenus'));
  assert.ok(manifest.permissions.includes('storage'));
  assert.ok(manifest.permissions.includes('webRequest'));
  assert.ok(manifest.permissions.includes('tabs'));
  assert.ok(manifest.optional_permissions.includes('cookies'));
  assert.deepEqual(manifest.host_permissions, ['http://*/*', 'https://*/*']);

  assert.deepEqual(manifest.background.scripts, ['bridge.js', 'sniff.js', 'background.js']);
  assert.deepEqual(manifest.content_scripts[0].js, ['sniff.js', 'content.js']);
});

test('firefox extension: dist/firefox unpacked directory is populated and valid', () => {
  const distFf = path.join(root, 'dist', 'firefox');
  assert.ok(fs.existsSync(distFf), 'dist/firefox must exist');

  const required = [
    'manifest.json',
    'background.js',
    'bridge.js',
    'sniff.js',
    'content.js',
    'popup.html',
    'popup.css',
    'popup.js',
    'icons/16.png',
    'icons/48.png',
    'icons/128.png'
  ];

  for (const file of required) {
    assert.ok(fs.existsSync(path.join(distFf, file)), `dist/firefox/${file} must exist`);
  }

  const manifest = JSON.parse(fs.readFileSync(path.join(distFf, 'manifest.json'), 'utf8'));
  assert.equal(manifest.browser_specific_settings?.gecko?.id, 'neodm@unlimitedera.com');
});
