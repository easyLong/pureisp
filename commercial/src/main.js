import { resolve } from 'node:path';
import { AccountStore } from './store.js';
import { createAccountService } from './service.js';
import { vaultKey } from './card-vault.js';

async function main() {
  const env = process.env;
  vaultKey(env.CARD_ENCRYPTION_KEY);
  if ([env.ADMIN_KEY, env.SESSION_SECRET].some(key => key?.toLowerCase() === env.CARD_ENCRYPTION_KEY.toLowerCase())) throw new Error('Use independent encryption and access keys');
  if (!['mysql', 'file'].includes(env.ACCOUNT_STORE)) throw new Error('ACCOUNT_STORE must be mysql or file');
  if (env.ACCOUNT_STORE === 'mysql' && !env.MYSQL_URL) throw new Error('MYSQL_URL is required');
  const store = new AccountStore({ directory: resolve(env.DATA_DIRECTORY || './data'), mysqlUrl: env.ACCOUNT_STORE === 'mysql' ? env.MYSQL_URL : undefined, sslCa: env.MYSQL_SSL_CA, cardEncryptionKey: env.CARD_ENCRYPTION_KEY });
  await store.init();
  const app = createAccountService({ store, sessionSecret: env.SESSION_SECRET, adminKey: env.ADMIN_KEY, renewalUrl: env.RENEWAL_URL, pollSeconds: Number(env.DB_POLL_SECONDS || 10), trustProxy: env.TRUST_PROXY === 'true' });
  const host = env.HOST || '127.0.0.1'; const port = Number(env.PORT || 3610);
  await new Promise((resolve, reject) => { app.server.once('error', reject); app.server.listen(port, host, resolve); });
  console.log(`PureISP license service 1.1.9 listening on ${host}:${port}`);
  let stopping = false;
  for (const event of ['SIGINT', 'SIGTERM']) process.on(event, async () => { if (stopping) return; stopping = true; await app.close(); });
}
main().catch(() => { console.error('Startup failed. Check server-only .env and database access.'); process.exitCode = 1; });
