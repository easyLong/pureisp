import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile, copyFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const script = fileURLToPath(new URL('../scripts/setup.js', import.meta.url));
function setup(directory) {
  const result = spawnSync(process.execPath, [script], { cwd: directory, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
}
test('setup adds a stable encryption key without rotating existing access or encryption keys', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'pureisp-setup-'));
  const original = 'SESSION_SECRET=' + '1'.repeat(64) + '\nADMIN_KEY=' + '2'.repeat(64) + '\nACCOUNT_STORE=file\n';
  await writeFile(join(directory, '.env'), original);
  setup(directory);
  const upgraded = await readFile(join(directory, '.env'), 'utf8');
  assert.ok(upgraded.startsWith(original));
  const encryptionKey = upgraded.match(/^CARD_ENCRYPTION_KEY=([a-f0-9]{64})$/m)[1];
  assert.notEqual(encryptionKey, '1'.repeat(64)); assert.notEqual(encryptionKey, '2'.repeat(64));
  setup(directory); assert.equal(await readFile(join(directory, '.env'), 'utf8'), upgraded);
  const fresh = await mkdtemp(join(tmpdir(), 'pureisp-new-setup-'));
  await copyFile(new URL('../.env.example', import.meta.url), join(fresh, '.env.example'));
  setup(fresh);
  const values = [...(await readFile(join(fresh, '.env'), 'utf8')).matchAll(/^(?:SESSION_SECRET|ADMIN_KEY|CARD_ENCRYPTION_KEY)=([a-f0-9]{64})$/gm)].map(match => match[1]);
  assert.equal(values.length, 3); assert.equal(new Set(values).size, 3);
});
