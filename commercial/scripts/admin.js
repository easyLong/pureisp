const [command, target, expiry] = process.argv.slice(2);
const base = process.env.ACCOUNT_API_URL || `http://127.0.0.1:${process.env.PORT || 3610}`;
const parsed = new URL(base);
if (parsed.protocol !== 'https:' && !(parsed.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(parsed.hostname))) throw new Error('Remote admin requires HTTPS');
const headers = { 'X-Admin-Key': process.env.ADMIN_KEY, 'Content-Type': 'application/json' };
async function request(path, method = 'GET', body) {
  const response = await fetch(base + path, { method, headers, body: body && JSON.stringify(body), redirect: 'error', signal: AbortSignal.timeout(15000) });
  const result = await response.json(); if (!response.ok) throw new Error(result.message); return result;
}
try {
  if (command === 'list') console.log(JSON.stringify(await request('/v1/admin/cards'), null, 2));
  else if (command === 'reveal' && /^[a-f0-9]{24}$/.test(target || '')) console.log(JSON.stringify(await request(`/v1/admin/cards/${target}/key`), null, 2));
  else if (command === 'create' && target !== undefined && expiry) console.log(JSON.stringify(await request('/v1/admin/cards', 'POST', { label: target, expiresAt: expiry }), null, 2));
  else if (['renew', 'disable'].includes(command) && /^[a-f0-9]{24}$/.test(target || '')) {
    let expiresAt = expiry;
    if (command === 'disable') { const { cards } = await request('/v1/admin/cards'); expiresAt = cards.find(c => c.id === target)?.expiresAt || new Date().toISOString(); }
    console.log(JSON.stringify(await request(`/v1/admin/cards/${target}/license`, 'PUT', { enabled: command === 'renew', expiresAt }), null, 2));
  } else { console.log('Usage: npm run admin -- list | create "LABEL" 2026-12-01T00:00:00+08:00 | reveal CARD_ID | renew CARD_ID ISO_DATE | disable CARD_ID'); process.exitCode = 1; }
} catch (e) { console.error(e.message); process.exitCode = 1; }
