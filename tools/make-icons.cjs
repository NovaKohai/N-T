'use strict';
// Regenerates the icons by invoking tools/apply-new-icon.ps1
const { spawnSync } = require('node:child_process');
const path = require('node:path');

const scriptPath = path.join(__dirname, 'apply-new-icon.ps1');
const result = spawnSync('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', scriptPath], {
  stdio: 'inherit'
});

if (result.status !== 0) {
  process.exit(result.status || 1);
}
