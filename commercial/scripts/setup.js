import { readFile, writeFile, access } from 'node:fs/promises';
import { randomBytes } from 'node:crypto';
try {
  await access('.env');
  const text = await readFile('.env', 'utf8');
  if (!/^CARD_ENCRYPTION_KEY=/m.test(text)) {
    await writeFile('.env', '\nCARD_ENCRYPTION_KEY=' + randomBytes(32).toString('hex') + '\n', { flag: 'a' });
    console.log('Added server-only card encryption key; existing settings retained.');
  } else console.log('.env already exists; retained.');
}
catch {
  let text = await readFile('.env.example', 'utf8');
  text = text.replace('SESSION_SECRET=CHANGE_ME', 'SESSION_SECRET=' + randomBytes(32).toString('hex'))
    .replace('ADMIN_KEY=CHANGE_ME', 'ADMIN_KEY=' + randomBytes(32).toString('hex'))
    .replace('CARD_ENCRYPTION_KEY=CHANGE_ME', 'CARD_ENCRYPTION_KEY=' + randomBytes(32).toString('hex'))
    .replace('RENEWAL_URL=https://account.example.com/renew', 'RENEWAL_URL=http://127.0.0.1:3610/renew');
  await writeFile('.env', text, { flag: 'wx', mode: 0o600 });
  console.log('Local .env created with independent random keys. Configure HTTPS URLs before customer deployment.');
}
