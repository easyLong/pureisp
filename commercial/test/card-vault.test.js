import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { encryptCard, decryptCard, vaultKey } from '../src/card-vault.js';
import { generateLicense, licenseHash } from '../src/security.js';
import { AccountStore } from '../src/store.js';
import { createAccountService } from '../src/service.js';

const secret = '3'.repeat(64);
test('encrypted cards reject tampering, wrong keys and substitution across accounts', () => {
  const key = generateLicense(), account = { id: 'test', keyHash: licenseHash(key) };
  const encrypted = encryptCard(key, account, secret);
  assert.equal(decryptCard(encrypted, account, secret), key);
  assert.notEqual(encryptCard(key, account, secret), encrypted, 'each encryption uses a fresh nonce');
  assert.ok(!encrypted.includes(key));
  const parts = encrypted.split('.'); const tag = Buffer.from(parts[2], 'base64url'); tag[0] ^= 1; parts[2] = tag.toString('base64url');
  assert.throws(() => decryptCard(parts.join('.'), account, secret));
  assert.throws(() => decryptCard(encrypted, account, '4'.repeat(64)));
  assert.throws(() => decryptCard(encrypted, { ...account, id: 'other' }, secret));
  assert.throws(() => encryptCard(generateLicense(), account, secret));
  assert.throws(() => vaultKey('bad'));
});

test('legacy card backfill preserves identity, paid expiry and status; hash-only cards remain explicit', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'pureisp-backfill-'));
  const store = new AccountStore({ directory, cardEncryptionKey: secret }); await store.init();
  const key = generateLicense(), id = 'a'.repeat(24);
  const account = { id, keyHash: licenseHash(key), hint: '…' + key.slice(-8), label: 'legacy', enabled: false, expiresAt: '2027-01-01T00:00:00.000Z', version: 2 };
  await store.create(account);
  assert.throws(() => store.revealCard(id), { code: 'CARD_NOT_RECOVERABLE' });
  const app = createAccountService({ store, sessionSecret: '1'.repeat(64), adminKey: '2'.repeat(64), renewalUrl: 'https://example.com/renew' });
  await new Promise(resolve => app.server.listen(0, '127.0.0.1', resolve));
  const base = 'http://127.0.0.1:' + app.server.address().port;
  const headers = { 'X-Admin-Key': '2'.repeat(64) };
  try {
    const old = await fetch(base + '/v1/admin/cards/' + id + '/key', { headers });
    assert.equal(old.status, 409); assert.equal((await old.json()).code, 'CARD_NOT_RECOVERABLE');
    const missing = await fetch(base + '/v1/admin/cards/' + 'b'.repeat(24) + '/key', { headers });
    assert.equal(missing.status, 404);
    await assert.rejects(store.saveCardKey(id, generateLicense()));
    assert.deepEqual(store.byId(id), account);
    await store.saveCardKey(id, key);
    const { encryptedKey, ...unchanged } = store.byId(id); assert.deepEqual(unchanged, account);
    assert.equal(store.revealCard(id), key);
    assert.ok(!(await readFile(join(directory, 'license-snapshot.json'), 'utf8')).includes(key));
    store.cardEncryptionKey = '4'.repeat(64);
    const failed = await fetch(base + '/v1/admin/cards/' + id + '/key', { headers });
    assert.equal(failed.status, 500); assert.equal((await failed.json()).code, 'CARD_DECRYPT_FAILED');
    const list = await fetch(base + '/v1/admin/cards?includeKeys=true', { headers });
    assert.equal(list.status, 200, 'one failed decryption does not block the administrator list');
    const card = (await list.json()).cards[0]; assert.ok(card.keyError); assert.equal(card.licenseKey, undefined);
    assert.equal(store.byHash(licenseHash(key)).id, id, 'decryption failures never affect hash-based validation');
  } finally { await app.close(); }
});

test('MySQL writes only ciphertext and retrieves it after a database reload', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'pureisp-cipher-db-'));
  let row, revision = 0;
  const queries = [];
  const pool = {
    async query(sql) {
      queries.push(sql);
      if (sql.startsWith('SELECT revision')) return [[{ revision }]];
      if (sql.startsWith('SELECT id')) return [[...(row ? [structuredClone(row)] : [])]];
      return [[]];
    },
    async execute(sql, params) {
      if (sql.startsWith('SELECT COLUMN')) return [[]];
      if (sql.startsWith('SELECT TRIGGER')) return [[{ TRIGGER_NAME: 'present' }]];
      if (sql.startsWith('SELECT version FROM pureisp_license_migrations')) return [[]];
      if (sql.startsWith('INSERT INTO pureisp_license_keys')) {
        assert.equal(params.length, 9); assert.equal((sql.match(/\?/g) || []).length, 9);
        const [id, keyHash, hint, label, enabled, expiresAt, version, encryptedKey, createdAt] = params;
        row = { id, keyHash, hint, label, enabled, expiresAt, version, encryptedKey, createdAt }; revision++;
      }
      return [{ affectedRows: 1 }];
    }, async end() {}
  };
  const store = new AccountStore({ directory, mysqlUrl: 'mysql://unused', pool, cardEncryptionKey: secret }); await store.init();
  assert.ok(queries.some(sql => sql.startsWith('ALTER TABLE')));
  const key = generateLicense();
  await store.create({ id: 'c'.repeat(24), keyHash: licenseHash(key), hint: '…' + key.slice(-8), label: 'test', enabled: true, expiresAt: '2027-01-01T00:00:00Z', version: 1 }, key);
  assert.ok(!JSON.stringify(row).includes(key));
  await store.refresh(true); assert.equal(store.revealCard(row.id), key);
  const restarted = new AccountStore({ directory, mysqlUrl: 'mysql://unused', pool, cardEncryptionKey: secret }); await restarted.init();
  assert.equal(restarted.revealCard(row.id), key);
  await restarted.close(); await store.close();
});
