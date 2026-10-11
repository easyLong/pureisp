// Demo license metadata. Production credentials must come from a server-only secret store.
const EGRESS_USERNAME = process.env.PUREISP_EGRESS_USERNAME || 'CONFIGURE_SERVER_SIDE';
const EGRESS_PASSWORD = process.env.PUREISP_EGRESS_PASSWORD || 'CONFIGURE_SERVER_SIDE';
const LICENSES = {
  'PUREISP-VIP-ATT-2026': {
    licenseKey: 'PUREISP-VIP-ATT-2026',
    active: true,
    planName: 'US Boston (AT&T Residential ISP 独享静态住宅)',
    carrier: 'AT&T Enterprises, LLC',
    asn: 'AS7018',
    city: 'Boston',
    region: 'Massachusetts',
    country: 'United States',
    egressIp: '12.12.110.239',
    expireAt: '2027-10-10T00:00:00Z',
    bandwidthRemainingGb: 9999,
    nodeConfig: {
      type: 'oxylabs-chained',
      frontNode: '❇️美国-T',
      egressName: 'Oxylabs-Static-US(AT&T)',
      server: 'disp.oxylabs.io',
      port: 8001,
      username: EGRESS_USERNAME,
      password: EGRESS_PASSWORD
    }
  },
  'PUREISP-PRO-US-WEST': {
    licenseKey: 'PUREISP-PRO-US-WEST',
    active: true,
    planName: 'US West (Comcast Residential ISP 独享住宅)',
    carrier: 'Comcast Cable Communications, LLC',
    asn: 'AS7922',
    city: 'San Jose',
    region: 'California',
    country: 'United States',
    egressIp: '73.189.21.104',
    expireAt: '2027-10-10T00:00:00Z',
    bandwidthRemainingGb: 50,
    nodeConfig: {
      type: 'oxylabs-chained',
      frontNode: '❇️美国-N',
      egressName: 'PureISP-Residential-CA(Comcast)',
      server: 'disp.oxylabs.io',
      port: 8001,
      username: EGRESS_USERNAME,
      password: EGRESS_PASSWORD
    }
  }
};

function getLicense(key) {
  if (!key) return null;
  return LICENSES[key.trim().toUpperCase()] || null;
}

module.exports = {
  getLicense,
  LICENSES
};
