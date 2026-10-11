import { readFile, writeFile } from 'node:fs/promises';
import { createCipheriv, randomBytes } from 'node:crypto';

function arg(name) {
  const index = process.argv.indexOf(name);
  if (index < 0 || !process.argv[index + 1]) throw new Error(`Missing ${name}`);
  return process.argv[index + 1];
}

const input = arg('--input');
const output = arg('--output');
const key = Buffer.from(arg('--key'), 'base64');
if (key.length !== 32) throw new Error('AES-256 key must be 32 bytes');
const nonce = randomBytes(12);
const cipher = createCipheriv('aes-256-gcm', key, nonce);
const ciphertext = Buffer.concat([cipher.update(await readFile(input)), cipher.final()]);
const tag = cipher.getAuthTag();
await writeFile(output, Buffer.concat([Buffer.from('PISPENC1', 'ascii'), nonce, tag, ciphertext]));
