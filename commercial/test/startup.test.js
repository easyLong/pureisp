import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { startupErrorHint } from '../src/startup-errors.js';

test('startup diagnostics identify database failures without revealing credentials or SQL', () => {
  const secret = 'database-password-that-must-not-appear';
  for (const code of ['ER_ACCESS_DENIED_ERROR', 'ER_SPECIFIC_ACCESS_DENIED_ERROR', 'ETIMEDOUT', 'ERR_INVALID_URL']) {
    const error = Object.assign(new Error('mysql://user:' + secret + '@host/db'), { code, sql: secret, stack: secret });
    const output = startupErrorHint(error);
    assert.ok(output.includes(code));
    assert.ok(!output.includes(secret));
    assert.ok(!output.includes('mysql://'));
  }
  assert.ok(!startupErrorHint(new Error(secret)).includes(secret));
});

test('startup reports an invalid encryption key and exits without printing key values', () => {
  const script = fileURLToPath(new URL('../src/main.js', import.meta.url));
  const result = spawnSync(process.execPath, [script], {
    encoding: 'utf8', timeout: 5000,
    env: { ...process.env, CARD_ENCRYPTION_KEY: 'invalid-secret-that-must-not-appear' }
  });
  assert.equal(result.status, 1, result.stderr);
  assert.match(result.stderr, /Startup failed during configuration/);
  assert.match(result.stderr, /CARD_ENCRYPTION_KEY.*32-byte hex key/);
  assert.ok(!result.stderr.includes('invalid-secret-that-must-not-appear'));
});

test('startup reports a malformed database URL and exits without revealing its value', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'pureisp-startup-'));
  const script = fileURLToPath(new URL('../src/main.js', import.meta.url));
  const result = spawnSync(process.execPath, [script], {
    encoding: 'utf8', timeout: 5000,
    env: { ...process.env, ACCOUNT_STORE: 'mysql', MYSQL_URL: 'malformed-private-url', DATA_DIRECTORY: directory,
      CARD_ENCRYPTION_KEY: '3'.repeat(64), SESSION_SECRET: '1'.repeat(64), ADMIN_KEY: '2'.repeat(64), MYSQL_SSL_CA: '' }
  });
  assert.equal(result.status, 1, result.stderr);
  assert.match(result.stderr, /database \/ data initialization/);
  assert.match(result.stderr, /ERR_INVALID_URL/);
  assert.ok(!result.stderr.includes('malformed-private-url'));
});
