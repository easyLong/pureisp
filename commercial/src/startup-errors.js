const configurationMessages = new Set([
  'Set CARD_ENCRYPTION_KEY to an independent 32-byte hex key',
  'Use independent encryption and access keys',
  'ACCOUNT_STORE must be mysql or file',
  'MYSQL_URL is required',
  'MYSQL_URL must be mysql://',
  'Set independent random SESSION_SECRET and ADMIN_KEY (32 bytes each)',
  'RENEWAL_URL requires HTTPS',
  'Invalid poll interval',
  'Invalid account snapshot',
  'Account revision missing'
]);

const errorHints = {
  ER_ACCESS_DENIED_ERROR: 'MySQL authentication failed. Check the database user and password in MYSQL_URL.',
  ER_DBACCESS_DENIED_ERROR: 'MySQL user cannot access this database. Check database permissions.',
  ER_BAD_DB_ERROR: 'The database does not exist. Create it before starting the service.',
  ER_TABLEACCESS_DENIED_ERROR: 'MySQL table permissions are insufficient. Initialization requires CREATE, ALTER and TRIGGER, plus SELECT, INSERT, UPDATE and DELETE.',
  ER_SPECIFIC_ACCESS_DENIED_ERROR: 'MySQL initialization permissions are insufficient. Check CREATE, ALTER and TRIGGER permissions.',
  ER_HOST_NOT_PRIVILEGED: 'MySQL rejected this server. Check the RDS IP whitelist and user host permissions.',
  ER_NOT_SUPPORTED_AUTH_MODE: 'MySQL authentication mode is unsupported. Check the database authentication settings.',
  ENOTFOUND: 'Hostname lookup failed. Check MYSQL_URL or HOST for a valid hostname.',
  EAI_AGAIN: 'DNS lookup temporarily failed. Check server DNS connectivity.',
  ECONNREFUSED: 'Connection refused. Check the RDS endpoint, port and network access.',
  ETIMEDOUT: 'Connection timed out. Check the RDS IP whitelist, VPC connectivity and firewall.',
  ECONNRESET: 'The connection was reset. Check database connectivity and TLS settings.',
  EADDRINUSE: 'The service port is already in use. Stop the duplicate service or change PORT.',
  EADDRNOTAVAIL: 'HOST is not an address available on this server.',
  EACCES: 'Permission denied. Check data directory permissions and the service port.',
  EPERM: 'Permission denied. Check data directory permissions.',
  ENOENT: 'A required file is missing. Check DATA_DIRECTORY and MYSQL_SSL_CA.',
  ERR_INVALID_URL: 'Invalid URL. Check MYSQL_URL and RENEWAL_URL; encode special characters in database credentials.',
  ERR_SOCKET_BAD_PORT: 'Invalid port. Check PORT and the database port.',
  HANDSHAKE_NO_SSL_SUPPORT: 'MySQL does not support the requested TLS connection. Check MYSQL_SSL_CA and database TLS settings.',
  CERT_HAS_EXPIRED: 'The database TLS certificate has expired.',
  UNABLE_TO_VERIFY_LEAF_SIGNATURE: 'Database TLS certificate verification failed. Check MYSQL_SSL_CA.',
  SELF_SIGNED_CERT_IN_CHAIN: 'Database TLS certificate verification failed. Check MYSQL_SSL_CA.'
};

// Never log raw database errors, SQL, URLs, input or stacks containing credentials.
export function startupErrorHint(error) {
  if (configurationMessages.has(error?.message)) return error.message;
  if (Object.hasOwn(errorHints, error?.code)) return '[' + error.code + '] ' + errorHints[error.code];
  return 'Unexpected startup failure. Check server-only .env, database access and the data snapshot.';
}
