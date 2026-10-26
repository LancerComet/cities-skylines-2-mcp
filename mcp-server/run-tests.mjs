import { readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

// Node 18 on Windows receives shell globs literally. Enumerate the intended
// test files here so every supported platform runs the same suite.
const root = dirname(fileURLToPath(import.meta.url));
const files = readdirSync(join(root, 'test'))
  .filter(name => name.endsWith('.test.mjs'))
  .sort()
  .map(name => join('test', name));
if (files.length === 0) throw new Error('No test files found');
const result = spawnSync(process.execPath, ['--test', ...files], {
  cwd: root, stdio: 'inherit', windowsHide: true,
});
if (result.error) throw result.error;
process.exit(result.status ?? 1);
