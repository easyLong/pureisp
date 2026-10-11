import { mkdir, readFile, writeFile, rename } from 'node:fs/promises';
import { join } from 'node:path';
import { createPool } from 'mysql2/promise';
import { ApiError } from './security.js';
import { encryptCard, decryptCard } from './card-vault.js';

// Requests read memory. Only account changes and the revision poll touch MySQL.
export class AccountStore {
  constructor({ directory, mysqlUrl, sslCa, pool, cardEncryptionKey }) {
    this.directory = directory; this.mysqlUrl = mysqlUrl; this.sslCa = sslCa;
    this.pool = pool; this.accounts = new Map(); this.hashIndex = new Map(); this.available = !mysqlUrl;
    this.cardEncryptionKey = cardEncryptionKey;
    this.revision = null; this.tail = Promise.resolve(); this.hasSnapshot = false;
  }
  async init() {
    await mkdir(this.directory, { recursive: true, mode: 0o700 });
    try {
      const data = JSON.parse(await readFile(join(this.directory, 'license-snapshot.json'), 'utf8'));
      if (data.schema !== 3 || !Array.isArray(data.accounts)) throw new Error('Invalid account snapshot');
      this.replaceAccounts(new Map(data.accounts.map(a => [a.id, { ...a, createdAt: a.createdAt || '1970-01-01T00:00:00.000Z' }]))); this.hasSnapshot = true;
    } catch (e) { if (e.code !== 'ENOENT') throw e; }
    if (this.mysqlUrl && !this.pool) {
      const url = new URL(this.mysqlUrl);
      if (url.protocol !== 'mysql:') throw new Error('MYSQL_URL must be mysql://');
      this.pool = createPool({ host: url.hostname, port: Number(url.port || 3306), user: decodeURIComponent(url.username), password: decodeURIComponent(url.password), database: decodeURIComponent(url.pathname.slice(1)), connectionLimit: 3, waitForConnections: true, queueLimit: 20, connectTimeout: 5000, dateStrings: true, timezone: 'Z', supportBigNumbers: true, bigNumberStrings: true, ...(this.sslCa ? { ssl: { ca: await readFile(this.sslCa, 'utf8'), rejectUnauthorized: true } } : {}) });
      this.pool.on('connection', connection => { connection.query("SET time_zone = '+00:00'"); });
    }
    if (this.pool) {
      try { await this.migrate(); await this.refresh(true); }
      catch (e) { this.available = false; if (!this.hasSnapshot) throw e; }
    } else await this.persist();
  }
  async migrate() {
    await this.pool.query('CREATE TABLE IF NOT EXISTS pureisp_license_keys (id VARCHAR(32) PRIMARY KEY, key_hash CHAR(64) NOT NULL UNIQUE, hint VARCHAR(24) NOT NULL, label VARCHAR(80) NOT NULL, enabled BOOLEAN NOT NULL DEFAULT FALSE, expires_at DATETIME(3) NULL, version INT NOT NULL DEFAULT 1)');
    const [createdAtColumns] = await this.pool.execute('SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = ? AND COLUMN_NAME = ?', ['pureisp_license_keys', 'created_at']);
    if (!createdAtColumns.length) await this.pool.query('ALTER TABLE pureisp_license_keys ADD COLUMN created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)');
    const [columns] = await this.pool.execute('SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = ? AND COLUMN_NAME = ?', ['pureisp_license_keys', 'encrypted_key']);
    if (!columns.length) await this.pool.query('ALTER TABLE pureisp_license_keys ADD COLUMN encrypted_key VARCHAR(512) NULL');
    await this.pool.query('CREATE TABLE IF NOT EXISTS pureisp_license_revision (id INT PRIMARY KEY, revision BIGINT UNSIGNED NOT NULL DEFAULT 0)');
    await this.pool.query('INSERT IGNORE INTO pureisp_license_revision (id, revision) VALUES (1, 0)');
    await this.pool.query('CREATE TABLE IF NOT EXISTS pureisp_license_migrations (version INT PRIMARY KEY, applied_at DATETIME(3) NOT NULL)');
    const [migrations] = await this.pool.execute('SELECT version FROM pureisp_license_migrations WHERE version = ?', [1]);
    if (!migrations.length) {
      await this.pool.query('UPDATE pureisp_license_keys SET created_at = DATE_SUB(UTC_TIMESTAMP(3), INTERVAL 1 SECOND) WHERE created_at > DATE_ADD(UTC_TIMESTAMP(3), INTERVAL 1 MINUTE)');
      await this.pool.query('INSERT IGNORE INTO pureisp_license_migrations (version, applied_at) VALUES (1, UTC_TIMESTAMP(3))');
    }
    for (const event of ['INSERT', 'UPDATE', 'DELETE']) {
      const trigger = 'pureisp_license_keys_' + event.toLowerCase();
      const [rows] = await this.pool.execute('SELECT TRIGGER_NAME FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = DATABASE() AND TRIGGER_NAME = ?', [trigger]);
      if (!rows.length) await this.pool.query(`CREATE TRIGGER ${trigger} AFTER ${event} ON pureisp_license_keys FOR EACH ROW UPDATE pureisp_license_revision SET revision = revision + 1 WHERE id = 1`);
    }
  }
  replaceAccounts(accounts) { this.accounts = accounts; this.hashIndex = new Map([...accounts.values()].map(a => [a.keyHash, a.id])); }
  byHash(hash) { return this.byId(this.hashIndex.get(hash)); }
  byId(id) { return this.accounts.get(id); }
  exclusive(job) { const result = this.tail.catch(() => {}).then(job); this.tail = result; return result; }
  async persist(accounts = this.accounts) {
    const path = join(this.directory, 'license-snapshot.json');
    await writeFile(path + '.tmp', JSON.stringify({ schema: 3, accounts: [...accounts.values()] }), { mode: 0o600 });
    await rename(path + '.tmp', path); this.hasSnapshot = true;
  }
  create(account, licenseKey) {
    return this.exclusive(async () => {
      if (this.byHash(account.keyHash)) throw new ApiError(409, 'CARD_EXISTS', '卡密已存在。');
      const newestCreated = Math.max(0, ...[...this.accounts.values()].map(existing => Date.parse(existing.createdAt || 0) || 0));
      account = { ...account, createdAt: new Date(Math.max(Date.now(), newestCreated + 1)).toISOString() };
      if (licenseKey !== undefined) account = { ...account, encryptedKey: encryptCard(licenseKey, account, this.cardEncryptionKey) };
      if (this.pool) {
        try {
          await this.pool.execute('INSERT INTO pureisp_license_keys (id, key_hash, hint, label, enabled, expires_at, version, encrypted_key, created_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)', [account.id, account.keyHash, account.hint, account.label, account.enabled, account.expiresAt ? new Date(account.expiresAt).toISOString().slice(0, 23).replace('T', ' ') : null, account.version, account.encryptedKey || null, account.createdAt.slice(0, 23).replace('T', ' ')]);
          this.available = true;
        } catch (e) { if (e.code === 'ER_DUP_ENTRY') throw new ApiError(409, 'CARD_EXISTS', '卡密已存在。'); this.available = false; throw new ApiError(503, 'DATABASE_UNAVAILABLE', '数据库暂时不可用，请稍后生成卡密。'); }
      }
      const accounts = new Map(this.accounts); accounts.set(account.id, account);
      await this.persist(accounts); this.replaceAccounts(accounts);
    });
  }
  revealCard(id) {
    const account = this.byId(id);
    if (!account) throw new ApiError(404, 'CARD_NOT_FOUND', '卡密不存在。');
    if (!account.encryptedKey) throw new ApiError(409, 'CARD_NOT_RECOVERABLE', '这张旧卡只保存了摘要，无法还原完整卡密；可从原始交付记录补入，或停用后生成新卡。');
    try { return decryptCard(account.encryptedKey, account, this.cardEncryptionKey); }
    catch { throw new ApiError(500, 'CARD_DECRYPT_FAILED', '完整卡密解密失败，请检查服务端加密密钥及备份。'); }
  }
  saveCardKey(id, licenseKey) {
    return this.exclusive(async () => {
      const previous = this.byId(id);
      if (!previous) throw new ApiError(404, 'CARD_NOT_FOUND', '卡密不存在。');
      const account = { ...previous, encryptedKey: encryptCard(licenseKey, previous, this.cardEncryptionKey) };
      if (this.pool) {
        try {
          const [result] = await this.pool.execute('UPDATE pureisp_license_keys SET encrypted_key = ? WHERE id = ? AND key_hash = ?', [account.encryptedKey, id, previous.keyHash]);
          if (!result.affectedRows) throw new ApiError(409, 'CARD_CHANGED', '卡密记录已变化，请刷新后重试。');
          this.available = true;
        } catch (e) { if (e instanceof ApiError) throw e; this.available = false; throw new ApiError(503, 'DATABASE_UNAVAILABLE', '数据库暂时不可用，本次补入未完成。'); }
      }
      const accounts = new Map(this.accounts); accounts.set(id, account);
      await this.persist(accounts); this.replaceAccounts(accounts);
    });
  }
  setLicense(id, enabled, expiresAt) {
    return this.exclusive(async () => {
      const previous = this.byId(id);
      if (!previous) throw new ApiError(404, 'CARD_NOT_FOUND', '卡密不存在。');
      const account = { ...previous, enabled, expiresAt, version: previous.version + (!enabled ? 1 : 0) };
      if (this.pool) {
        try {
          const [result] = await this.pool.execute('UPDATE pureisp_license_keys SET enabled = ?, expires_at = ?, version = ? WHERE id = ?', [enabled, expiresAt ? new Date(expiresAt).toISOString().slice(0, 23).replace('T', ' ') : null, account.version, id]);
          if (!result.affectedRows) throw new ApiError(404, 'CARD_NOT_FOUND', '卡密不存在。');
          this.available = true;
        } catch (e) { if (e instanceof ApiError) throw e; this.available = false; throw new ApiError(503, 'DATABASE_UNAVAILABLE', '数据库暂时不可用，本次开通或续费未完成。'); }
      }
      const accounts = new Map(this.accounts); accounts.set(id, account);
      await this.persist(accounts); this.replaceAccounts(accounts); return account;
    });
  }
  refresh(force = false) {
    return this.exclusive(async () => {
      if (!this.pool) return;
      try {
        const [revisions] = await this.pool.query('SELECT revision FROM pureisp_license_revision WHERE id = 1');
        if (!revisions.length) throw new Error('Account revision missing');
        const revision = String(revisions[0].revision);
        if (!force && revision === this.revision) { this.available = true; return; }
        const [rows] = await this.pool.query('SELECT id, key_hash AS keyHash, hint, label, enabled, expires_at AS expiresAt, version, encrypted_key AS encryptedKey, created_at AS createdAt FROM pureisp_license_keys');
        const accounts = new Map(rows.map(a => [a.id, { ...a, enabled: Boolean(a.enabled), expiresAt: a.expiresAt ? new Date(a.expiresAt.replace(' ', 'T') + 'Z').toISOString() : null, createdAt: a.createdAt ? new Date(a.createdAt.replace(' ', 'T') + 'Z').toISOString() : '1970-01-01T00:00:00.000Z' }]));
        await this.persist(accounts); this.replaceAccounts(accounts); this.revision = revision; this.available = true;
      } catch (e) { this.available = false; throw e; }
    });
  }
  async close() { await this.tail.catch(() => {}); if (this.pool) await this.pool.end(); }
}
