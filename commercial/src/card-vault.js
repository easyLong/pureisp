import { createCipheriv, createDecipheriv, randomBytes } from 'node:crypto';
import { licenseHash, normalizeLicense } from './security.js';

export function vaultKey(value) {
  if (!/^[a-f0-9]{64}$/i.test(value || '')) throw new Error('Set CARD_ENCRYPTION_KEY to an independent 32-byte hex key');
  return Buffer.from(value, 'hex');
}
function context(account) { return Buffer.from('pureisp-card:' + account.id + ':' + account.keyHash); }
export function encryptCard(value, account, secret) {
  const key = normalizeLicense(value);
  if (licenseHash(key) !== account.keyHash) throw new Error('Card hash mismatch');
  const nonce = randomBytes(12);
  const cipher = createCipheriv('aes-256-gcm', vaultKey(secret), nonce);
  cipher.setAAD(context(account));
  const encrypted = Buffer.concat([cipher.update(key, 'utf8'), cipher.final()]);
  return ['v1', nonce.toString('base64url'), cipher.getAuthTag().toString('base64url'), encrypted.toString('base64url')].join('.');
}
export function decryptCard(value, account, secret) {
  const parts = String(value).split('.');
  if (parts.length !== 4 || parts[0] !== 'v1') throw new Error('Invalid card envelope');
  const [, nonce, tag, encrypted] = parts.map((part, i) => i ? Buffer.from(part, 'base64url') : part);
  if (nonce.length !== 12 || tag.length !== 16) throw new Error('Invalid card envelope');
  const cipher = createDecipheriv('aes-256-gcm', vaultKey(secret), nonce);
  cipher.setAAD(context(account)); cipher.setAuthTag(tag);
  const key = Buffer.concat([cipher.update(encrypted), cipher.final()]).toString('utf8');
  if (licenseHash(key) !== account.keyHash) throw new Error('Card hash mismatch');
  return key;
}
