// Import a locally retained delivery record. Never print its card or credentials.
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { AccountStore } from '../src/store.js';
import { licenseHash } from '../src/security.js';
import { vaultKey } from '../src/card-vault.js';

let store;
try {
  const file = process.argv[2];
  if (!file) throw Error('Usage: node --env-file=.env scripts/import-card.js CARD_FILE');
  vaultKey(process.env.CARD_ENCRYPTION_KEY);
  if (!['mysql', 'file'].includes(process.env.ACCOUNT_STORE)) throw Error('Invalid account store');
  if (process.env.ACCOUNT_STORE === 'mysql' && !process.env.MYSQL_URL) throw Error('MySQL connection required');
  const text = await readFile(file, 'utf8');
  const keys = [...new Set((text.match(/PUREISP-(?:[A-F0-9]{8}-){3}[A-F0-9]{8}/gi) || []).map(key => key.toUpperCase()))];
  if (keys.length !== 1) throw Error('The file must contain exactly one complete card');
  store = new AccountStore({ directory: resolve(process.env.DATA_DIRECTORY || './data'), mysqlUrl: process.env.ACCOUNT_STORE === 'mysql' ? process.env.MYSQL_URL : undefined, sslCa: process.env.MYSQL_SSL_CA, cardEncryptionKey: process.env.CARD_ENCRYPTION_KEY });
  await store.init();
  const account = store.byHash(licenseHash(keys[0]));
  if (!account) throw Error('No existing card matches this delivery record');
  await store.saveCardKey(account.id, keys[0]);
  console.log('Stored the matching card in encrypted form. Expiry, status and ID unchanged.');
} catch {
  console.error('Card import failed. Check the file, encryption key and database; no card was printed.'); process.exitCode = 1;
} finally { if (store) await store.close(); }
