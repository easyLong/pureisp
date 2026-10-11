import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { randomBytes, randomInt } from 'node:crypto';
import { isIP } from 'node:net';
import { ApiError, normalizeLicense, licenseHash, generateLicense, equalSecret, makeToken, readToken } from './security.js';

export function createAccountService({ store, sessionSecret, adminKey, renewalUrl, pollSeconds = 10, trustProxy = false, now = Date.now }) {
  if (!/^[a-f0-9]{64,}$/i.test(sessionSecret) || !/^[a-f0-9]{64,}$/i.test(adminKey) || equalSecret(sessionSecret, adminKey)) throw new Error('Set independent random SESSION_SECRET and ADMIN_KEY (32 bytes each)');
  const renewal = new URL(renewalUrl);
  if (renewal.protocol !== 'https:' && !(renewal.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(renewal.hostname))) throw new Error('RENEWAL_URL requires HTTPS');
  if (!Number.isFinite(pollSeconds) || pollSeconds < 10) throw new Error('Invalid poll interval');
  const rate = new Map(); let syncing = false;
  function limit(key) {
    const time = now(); const record = rate.get(key);
    if (record && record.until > time) { if (++record.count > 20) throw new ApiError(429, 'RATE_LIMIT', '尝试过于频繁，请一分钟后重试。'); }
    else rate.set(key, { until: time + 60000, count: 1 });
    if (rate.size > 10000) for (const [k, v] of rate) if (v.until < time) rate.delete(k);
  }
  function view(account) {
    const expiry = Date.parse(account.expiresAt); const deadline = expiry;
    const status = !account.enabled ? (account.expiresAt ? 'disabled' : 'pending') : (!Number.isFinite(expiry) ? 'pending' : (now() >= deadline ? 'expired' : 'active'));
    return { id: account.id, hint: account.hint, label: account.label, status, createdAt: account.createdAt || '1970-01-01T00:00:00.000Z', expiresAt: Number.isFinite(expiry) ? new Date(expiry).toISOString() : null,
      validUntil: Number.isFinite(deadline) ? new Date(deadline).toISOString() : null,
      serverTime: new Date(now()).toISOString(), cached: !store.available, renewalUrl };
  }
  function requireActive(result) {
    if (result.status !== 'active') throw new ApiError(403, result.status === 'expired' ? 'CARD_EXPIRED' : 'CARD_INACTIVE', result.status === 'expired' ? '卡密已到期，请续费。' : '卡密尚未开通或已停用，请联系管理员。');
  }
  function authorized(req) {
    const value = req.headers.authorization || '';
    if (!value.startsWith('Bearer ')) throw new ApiError(401, 'SESSION_INVALID', '请先验证卡密。');
    const claims = readToken(value.slice(7), sessionSecret, now()); const account = store.byId(claims.id);
    if (!account || account.version !== claims.version) throw new ApiError(401, 'SESSION_INVALID', '请重新验证卡密。');
    return account;
  }
  async function body(req) {
    if (!/^application\/json(?:;|$)/i.test(req.headers['content-type'] || '')) throw new ApiError(415, 'JSON_REQUIRED', '请使用 JSON 请求。');
    let size = 0; const chunks = [];
    for await (const chunk of req) { size += chunk.length; if (size > 4096) throw new ApiError(413, 'BODY_TOO_LARGE', '请求内容过大。'); chunks.push(chunk); }
    try { const result = JSON.parse(Buffer.concat(chunks).toString()); if (!result || typeof result !== 'object' || Array.isArray(result)) throw new Error(); return result; }
    catch { throw new ApiError(400, 'INVALID_JSON', '请求格式错误。'); }
  }
  function validExpiry(value, enabled) {
    const expiry = typeof value === 'string' && /(?:Z|[+-]\d{2}:\d{2})$/.test(value) ? Date.parse(value) : NaN;
    if (!Number.isFinite(expiry) || (enabled && expiry <= now())) throw new ApiError(400, 'INVALID_EXPIRY', '有效期必须为带时区的未来时间。');
    return new Date(expiry).toISOString();
  }
  function send(res, status, value) {
    res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' }); res.end(JSON.stringify(value));
  }
  const server = createServer(async (req, res) => {
    try {
      const url = new URL(req.url, 'http://localhost');
      const path = url.pathname;
      if (req.method === 'GET' && path === '/health') return send(res, 200, { service: 'pureisp-account-service', version: '1.1.9', databaseAvailable: store.available });
      if (req.method === 'GET' && path === '/renew') {
        res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
        return res.end(await readFile(new URL('../public/renew.html', import.meta.url)));
      }
      if (req.method === 'GET' && path === '/admin') {
        res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', 'Content-Security-Policy': "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'" });
        return res.end(await readFile(new URL('../public/admin.html', import.meta.url)));
      }
      if (req.method === 'POST' && path === '/v1/activate') {
        let clientIp = req.socket.remoteAddress;
        if (trustProxy && ['127.0.0.1', '::1', '::ffff:127.0.0.1'].includes(clientIp)) {
          const forwarded = String(req.headers['x-forwarded-for'] || '').split(',').pop().trim();
          if (isIP(forwarded)) clientIp = forwarded;
        }
        limit('ip:' + clientIp);
        const input = await body(req); const keyHash = licenseHash(input.licenseKey);
        const account = store.byHash(keyHash);
        if (!account) throw new ApiError(401, 'CARD_INVALID', '授权卡密无效，请检查后重试。');
        const result = view(account); requireActive(result);
        return send(res, 200, { ...result, token: makeToken(account, sessionSecret, now()) });
      }
      if (req.method === 'GET' && path === '/v1/me') {
        const account = authorized(req); const result = view(account); requireActive(result);
        return send(res, 200, { ...result, token: makeToken(account, sessionSecret, now()) });
      }
      if (path.startsWith('/v1/admin/')) {
        if (!equalSecret(req.headers['x-admin-key'], adminKey)) throw new ApiError(401, 'ADMIN_REQUIRED', '管理接口需要授权。');
        if (req.method === 'GET' && path === '/v1/admin/cards') {
          const includeKeys = url.searchParams.get('includeKeys') === 'true';
          const cards = [...store.accounts.values()].map(a => {
            const result = { ...view(a), keyStored: Boolean(a.encryptedKey) };
            if (includeKeys && a.encryptedKey) {
              try { result.licenseKey = store.revealCard(a.id); }
              catch { result.keyError = '完整卡密读取失败，请检查服务端加密密钥。'; }
            }
            return result;
          });
          return send(res, 200, { cards });
        }
        if (req.method === 'POST' && path === '/v1/admin/cards') {
          const input = await body(req);
          if (input.label != null && typeof input.label !== 'string') throw new ApiError(400, 'INVALID_LABEL', '备注最多 80 个字符。');
          let label = (input.label || '').trim();
          if (label.length > 80) throw new ApiError(400, 'INVALID_LABEL', '备注最多 80 个字符。');
          if (!label) label = '用户' + randomInt(100000, 1000000);
          const expiresAt = validExpiry(input.expiresAt, true);
          const licenseKey = generateLicense();
          const account = { id: randomBytes(12).toString('hex'), keyHash: licenseHash(licenseKey), hint: '…' + licenseKey.slice(-8), label, enabled: true, expiresAt, version: 1 };
        await store.create(account, licenseKey);
          return send(res, 201, { ...view(account), licenseKey });
        }
        const keyMatch = path.match(/^\/v1\/admin\/cards\/([a-f0-9]{24})\/key$/);
        if (keyMatch && req.method === 'GET') return send(res, 200, { licenseKey: store.revealCard(keyMatch[1]) });
        const match = path.match(/^\/v1\/admin\/cards\/([a-f0-9]{24})\/license$/);
        if (match && req.method === 'PUT') {
          const input = await body(req);
          if (typeof input.enabled !== 'boolean') throw new ApiError(400, 'INVALID_LICENSE', 'enabled 必须为布尔值。');
          const expiresAt = validExpiry(input.expiresAt, input.enabled);
          return send(res, 200, view(await store.setLicense(match[1], input.enabled, expiresAt)));
        }
      }
      throw new ApiError(404, 'NOT_FOUND', '接口不存在。');
    } catch (e) {
      if (!res.headersSent) send(res, e instanceof ApiError ? e.status : 500, { code: e instanceof ApiError ? e.code : 'INTERNAL_ERROR', message: e instanceof ApiError ? e.message : '服务暂时不可用，请稍后重试。' });
      else res.end();
    }
  });
  server.requestTimeout = 10000; server.headersTimeout = 10000;
  async function sync(force = false) {
    if (syncing) return; syncing = true;
    try { await store.refresh(force); } finally { syncing = false; }
  }
  const poll = setInterval(() => sync().catch(() => {}), pollSeconds * 1000); poll.unref();
  const nightly = setInterval(() => sync(true).catch(() => {}), 86400000); nightly.unref();
  return { server, sync, async close() { clearInterval(poll); clearInterval(nightly); await new Promise(resolve => server.close(resolve)); await store.close(); } };
}
