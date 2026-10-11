import { randomBytes, timingSafeEqual, createHmac, createHash } from 'node:crypto';
export class ApiError extends Error {
  constructor(status, code, message) { super(message); this.status = status; this.code = code; }
}
export function normalizeLicense(value) {
  if (typeof value !== 'string' || value.length > 100) throw new ApiError(400, 'INVALID_CARD', '请输入正确的授权卡密。');
  const key = value.trim().toUpperCase();
  if (!/^PUREISP-(?:[A-F0-9]{8}-){3}[A-F0-9]{8}$/.test(key)) throw new ApiError(400, 'INVALID_CARD', '请输入正确的授权卡密。');
  return key;
}
export function licenseHash(value) { return createHash('sha256').update(normalizeLicense(value)).digest('hex'); }
export function generateLicense() {
  const hex = randomBytes(16).toString('hex').toUpperCase();
  return 'PUREISP-' + hex.match(/.{8}/g).join('-');
}
export function equalSecret(left, right) {
  const a = Buffer.from(left || ''); const b = Buffer.from(right || '');
  return a.length === b.length && timingSafeEqual(a, b);
}
export function makeToken(account, secret, now) {
  const data = Buffer.from(JSON.stringify({ id: account.id, version: account.version, exp: Math.floor(now / 1000) + 30 * 86400 })).toString('base64url');
  return data + '.' + createHmac('sha256', secret).update(data).digest('base64url');
}
export function readToken(value, secret, now) {
  if (typeof value !== 'string' || value.length > 2048) throw new ApiError(401, 'SESSION_INVALID', '请重新验证卡密。');
  const parts = value.split('.'); const [data, sig] = parts;
  if (parts.length !== 2 || !data || !sig || !equalSecret(sig, createHmac('sha256', secret).update(data).digest('base64url'))) throw new ApiError(401, 'SESSION_INVALID', '请重新验证卡密。');
  let claims; try { claims = JSON.parse(Buffer.from(data, 'base64url').toString()); } catch { throw new ApiError(401, 'SESSION_INVALID', '请重新验证卡密。'); }
  if (!Number.isFinite(claims.exp) || claims.exp * 1000 <= now || typeof claims.id !== 'string') throw new ApiError(401, 'SESSION_EXPIRED', '授权已失效，请重新验证卡密。');
  return claims;
}
