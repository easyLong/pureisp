import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { AccountStore } from '../src/store.js';

test('file persistence failure never activates an account only in memory', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'pureisp-persistence-'));
  const store = new AccountStore({ directory }); await store.init();
  await store.create({ id: 'test', keyHash: '1'.repeat(64), hint: 'test', label: '', enabled: false, expiresAt: null, version: 1 });
  const persist = store.persist.bind(store);
  store.persist = async () => { throw Error('disk write failure'); };
  await assert.rejects(store.setLicense('test', true, '2027-01-01T00:00:00Z'));
  assert.equal(store.byId('test').enabled, false);
  await assert.rejects(store.create({ id: 'new', keyHash: '2'.repeat(64), hint: 'new', label: '', enabled: false, expiresAt: null, version: 1 }));
  assert.equal(store.byId('new'), undefined);
  store.persist = persist; await store.close();
});
