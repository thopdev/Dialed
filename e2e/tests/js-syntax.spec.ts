import { expect, test } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { copyFileSync, mkdtempSync, readdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// A merge that drops a closing brace in a script breaks the whole app at start-up and no .NET test notices (it happened to platform.js).
// Every script the app loads must at least parse; modules are checked as modules.
const wwwroot = join(dirname(fileURLToPath(import.meta.url)), '../../src/Dialed.Web/wwwroot');
const modules = readdirSync(join(wwwroot, 'js')).filter(f => f.endsWith('.js')).map(f => join('js', f));
const scripts = ['push-handlers.js', 'service-worker.js', 'service-worker.published.js'];

for (const file of [...modules, ...scripts]) {
  test(`${file} parses`, () => {
    const dir = mkdtempSync(join(tmpdir(), 'dialed-js-'));
    const copy = join(dir, file.endsWith('.js') && modules.includes(file) ? 'check.mjs' : 'check.js');
    copyFileSync(join(wwwroot, file), copy);
    expect(() => execFileSync(process.execPath, ['--check', copy], { stdio: 'pipe' })).not.toThrow();
  });
}
