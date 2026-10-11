import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { AccountStore } from '../src/store.js';
import { createAccountService } from '../src/service.js';
import { makeToken, readToken, generateLicense, licenseHash } from '../src/security.js';

const secret = '1'.repeat(64), admin = '2'.repeat(64), cardEncryptionKey = '4'.repeat(64);
test('card generation → activation → renewal → disable → fixed expiry without registration', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'pureisp-cards-'));
  const store = new AccountStore({ directory, cardEncryptionKey }); await store.init();
  let time = Date.parse('2026-10-11T00:00:00Z');
  const app = createAccountService({ store, sessionSecret: secret, adminKey: admin, renewalUrl: 'https://account.example.com/renew', now: () => time });
  await new Promise(resolve => app.server.listen(0, '127.0.0.1', resolve));
  const base = 'http://127.0.0.1:' + app.server.address().port;
  async function request(path, body, auth = {}, method = body ? 'POST' : 'GET') {
    const response = await fetch(base + path, { method, headers: { 'Content-Type': 'application/json', ...auth }, body: body && JSON.stringify(body) });
    return { status: response.status, body: await response.json() };
  }
  let id;
  try {
    assert.equal((await request('/v1/me')).status, 401);
    assert.equal((await request('/v1/admin/cards')).status, 401);
    assert.equal((await request('/v1/register', {})).status, 404);
    assert.equal((await request('/v1/login', {})).status, 404);
    assert.equal((await request('/v1/activate', { licenseKey: generateLicense() })).status, 401);
    assert.equal((await request('/v1/activate', { licenseKey: ['invalid'] })).status, 400);
    const minted = await request('/v1/admin/cards', { label: '<script>alert(1)</script>', expiresAt: '2026-10-12T00:00:00Z' }, { 'X-Admin-Key': admin });
    assert.equal(minted.status, 201); const key = minted.body.licenseKey; id = minted.body.id;
    assert.equal(minted.body.label, '<script>alert(1)</script>', 'provided label is retained');
    for (const label of ['', '   ', undefined]) {
      const automatic = await request('/v1/admin/cards', { label, expiresAt: '2026-10-12T00:00:00Z' }, { 'X-Admin-Key': admin });
      assert.equal(automatic.status, 201);
      assert.match(automatic.body.label, /^用户\d{6}$/);
      assert.equal(store.byId(automatic.body.id).label, automatic.body.label, 'generated label is saved with the card');
    }
    assert.match(key, /^PUREISP-(?:[A-F0-9]{8}-){3}[A-F0-9]{8}$/);
    const activated = await request('/v1/activate', { licenseKey: '  ' + key.toLowerCase() + '  ' }); assert.equal(activated.status, 200);
    assert.equal(activated.body.validUntil, activated.body.expiresAt, 'authorization ends at the paid expiry');
    const auth = { Authorization: 'Bearer ' + activated.body.token };
    const keyPath = '/v1/admin/cards/' + id + '/key';
    assert.equal((await request(keyPath)).status, 401, 'complete cards require administrator authentication');
    assert.equal((await request(keyPath, null, auth)).status, 401, 'a client session cannot reveal complete cards');
    assert.equal((await request(keyPath, null, { 'X-Admin-Key': 'wrong' })).status, 401);
    const revealed = await request(keyPath, null, { 'X-Admin-Key': admin });
    assert.equal(revealed.status, 200); assert.equal(revealed.body.licenseKey, key);
    assert.ok(!JSON.stringify(activated.body).includes('encryptedKey'));
    assert.ok(!JSON.stringify(activated.body).includes('keyStored'));
    const snapshot = await readFile(join(directory, 'license-snapshot.json'), 'utf8');
    assert.ok(!snapshot.includes(key)); assert.equal(store.byId(id).keyHash, licenseHash(key));
    time = Date.parse('2026-10-11T23:59:59Z');
    assert.equal((await request('/v1/me', null, auth)).status, 200, 'usable immediately before paid expiry');
    time += 1000;
    assert.equal((await request('/v1/me', null, auth)).status, 403, 'rejected at paid expiry without an extra day');
    assert.equal((await request('/v1/activate', { licenseKey: key })).status, 403, 'expired card cannot reactivate');
    const path = '/v1/admin/cards/' + id + '/license';
    assert.equal((await request(path, { enabled: true, expiresAt: '2026-11-12T00:00:00Z' }, { 'X-Admin-Key': admin }, 'PUT')).status, 200);
    assert.equal((await request('/v1/me', null, auth)).status, 200, 'renewal works with existing card');
    await request(path, { enabled: false, expiresAt: '2026-11-12T00:00:00Z' }, { 'X-Admin-Key': admin }, 'PUT');
    assert.equal((await request('/v1/me', null, auth)).status, 401, 'disable revokes existing token');
    assert.equal((await request('/v1/activate', { licenseKey: key })).status, 403);
    assert.equal((await request(keyPath, null, { 'X-Admin-Key': admin })).body.licenseKey, key, 'disabled cards remain retrievable for administration');
    assert.equal((await request(path, { enabled: true, expiresAt: '2026-12-01' }, { 'X-Admin-Key': admin }, 'PUT')).status, 400);
    const cards = await request('/v1/admin/cards', null, { 'X-Admin-Key': admin });
    assert.ok(!JSON.stringify(cards).includes('keyHash')); assert.ok(!JSON.stringify(cards).includes(key));
    assert.ok(!JSON.stringify(cards).includes('encryptedKey')); assert.equal(cards.body.cards.find(c => c.id === id).keyStored, true);
    const fullList = await request('/v1/admin/cards?includeKeys=true', null, { 'X-Admin-Key': admin });
    assert.equal(fullList.body.cards.find(c => c.id === id).licenseKey, key, 'administrator can display complete cards in one request');
    assert.ok(!JSON.stringify(fullList).includes('encryptedKey'));
    assert.equal((await request('/v1/admin/cards?includeKeys=true')).status, 401);
    assert.equal((await request('/v1/admin/cards?includeKeys=true', null, auth)).status, 401, 'client sessions cannot list complete cards');
    assert.equal((await fetch(base + '/admin')).status, 200);
    assert.equal((await fetch(base + '/renew')).status, 200);
  } finally { await app.close(); }
  const restarted = new AccountStore({ directory, cardEncryptionKey }); await restarted.init();
  assert.equal(restarted.byId(id).enabled, false, 'card state survives restart');
  assert.match(restarted.revealCard(id), /^PUREISP-/); await restarted.close();
});

test('MySQL revision polling, direct edits, outage snapshots and failed writes', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'pureisp-db-'));
  let revision = 1, failed = false, rowReads = 0;
  let rows = [{ id: 'id1', keyHash: '0'.repeat(64), hint: '…12345678', label: 'existing', enabled: 1, expiresAt: '2026-12-01 00:00:00.000', version: 1 }];
  const pool = {
    async query(sql) {
      if (failed) throw Error('database offline');
      if (sql.startsWith('SELECT revision')) return [[{ revision }]];
      if (sql.startsWith('SELECT id')) { rowReads++; return [structuredClone(rows)]; }
      return [[]];
    },
    async execute(sql, parameters) {
      if (failed) throw Error('database offline');
      assert.equal((sql.match(/\?/g) || []).length, parameters.length, 'SQL placeholders match bound values');
      if (sql.startsWith('SELECT version FROM pureisp_license_migrations')) return [[]];
      if (sql.startsWith('SELECT TRIGGER')) return [[{ TRIGGER_NAME: 'present' }]];
      return [{ affectedRows: 1 }];
    }, async end() {}
  };
  const store = new AccountStore({ directory, mysqlUrl: 'mysql://unused', pool }); await store.init();
  assert.equal(rowReads, 1);
  await store.refresh(); await store.refresh(); assert.equal(rowReads, 1, 'unchanged revision does not reload account rows');
  rows[0].expiresAt = '2027-01-01 00:00:00.000'; revision++;
  await store.refresh(); assert.equal(store.byId('id1').expiresAt, '2027-01-01T00:00:00.000Z');
  await store.create({ id: 'new-card', keyHash: '3'.repeat(64), hint: 'test', label: 'SQL binding check', enabled: true, expiresAt: '2027-01-01T00:00:00.000Z', version: 1 });
  assert.equal(store.byHash('3'.repeat(64)).id, 'new-card');
  failed = true;
  await assert.rejects(store.refresh()); assert.equal(store.byId('id1').enabled, true);
  await assert.rejects(store.setLicense('id1', false, '2027-01-01T00:00:00.000Z'), { code: 'DATABASE_UNAVAILABLE' });
  assert.equal(store.byId('id1').enabled, true, 'failed writes never become successful cache changes');
  const restarted = new AccountStore({ directory, mysqlUrl: 'mysql://unused', pool }); await restarted.init();
  assert.equal(restarted.available, false); assert.equal(restarted.byId('id1').enabled, true, 'restart uses last valid snapshot');
  const app = createAccountService({ store: restarted, sessionSecret: secret, adminKey: admin, renewalUrl: 'https://account.example.com/renew', now: () => Date.parse('2026-10-11T00:00:00Z') });
  await new Promise(resolve => app.server.listen(0, '127.0.0.1', resolve));
  const token = makeToken(restarted.byId('id1'), secret, Date.parse('2026-10-11T00:00:00Z'));
  const response = await fetch('http://127.0.0.1:' + app.server.address().port + '/v1/me', { headers: { Authorization: 'Bearer ' + token } });
  assert.equal(response.status, 200); assert.equal((await response.json()).cached, true);
  await app.close(); await store.close();
});

test('signed session rejects tampering and expiry', () => {
  const account = { id: 'test', version: 1 }; const token = makeToken(account, secret, 0);
  assert.equal(readToken(token, secret, 1000).id, account.id);
  assert.throws(() => readToken(token + 'x', secret, 1000));
  assert.throws(() => readToken(token, secret, 31 * 86400000));
});

test('invalid body, malformed JSON and wrong admin key are rejected', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'pureisp-input-'));
  const store = new AccountStore({ directory, cardEncryptionKey }); await store.init();
  const app = createAccountService({ store, sessionSecret: secret, adminKey: admin, renewalUrl: 'https://account.example.com/renew' });
  await new Promise(resolve => app.server.listen(0, '127.0.0.1', resolve));
  const url = 'http://127.0.0.1:' + app.server.address().port;
  try {
    assert.equal((await fetch(url + '/v1/activate', { method: 'POST', body: '{}' })).status, 415);
    assert.equal((await fetch(url + '/v1/activate', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{' })).status, 400);
    assert.equal((await fetch(url + '/v1/activate', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ licenseKey: 'a'.repeat(5000) }) })).status, 413);
    assert.equal((await fetch(url + '/v1/admin/cards', { headers: { 'X-Admin-Key': 'wrong' } })).status, 401);
  } finally { await app.close(); }
});
