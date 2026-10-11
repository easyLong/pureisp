// Test-only account server: no real database, upstream nodes or customer credentials.
import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
import { AccountStore } from '../src/store.js';
import { createAccountService } from '../src/service.js';
import { generateLicense, licenseHash } from '../src/security.js';
const directory = process.argv[2];
const store = new AccountStore({ directory: join(directory, 'server-data'), cardEncryptionKey: randomBytes(32).toString('hex') }); await store.init();
const adminKey = randomBytes(32).toString('hex');
const licenseKey = generateLicense(), cardId = randomBytes(12).toString('hex');
await store.create({ id: cardId, keyHash: licenseHash(licenseKey), hint: '…' + licenseKey.slice(-8), label: 'QA fixture', enabled: true, expiresAt: new Date(Date.now() + 3600000).toISOString(), version: 1 }, licenseKey);
const app = createAccountService({ store, sessionSecret: randomBytes(32).toString('hex'), adminKey, renewalUrl: 'https://account.example.com/renew' });
await new Promise(resolve => app.server.listen(0, '127.0.0.1', resolve));
const serviceUrl = 'http://127.0.0.1:' + app.server.address().port;
await writeFile(join(directory, 'account-service.json'), JSON.stringify({ serviceUrl, renewalUrl: 'https://account.example.com/renew' }));
await writeFile(join(directory, 'fixture.json'), JSON.stringify({ serviceUrl, adminKey, licenseKey, cardId }));
process.on('SIGTERM', () => app.close());
console.log('Account client fixture ready');
