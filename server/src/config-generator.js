const fs = require('fs');
const path = require('path');

function generateClashYaml(license) {
  const egressNode = license.nodeConfig;
  const frontNodeName = egressNode.frontNode || '❇️美国-T';

  // Base profile path from user's current environment or fallback definition
  let transitProxies = [];
  const baseProfilePath = path.join(
    process.env.APPDATA || 'C:\\Users\\Administrator\\AppData\\Roaming',
    'io.github.clash-verge-rev.clash-verge-rev',
    'profiles',
    'LC8RvVCmlJoz.yaml'
  );

  if (fs.existsSync(baseProfilePath)) {
    try {
      const content = fs.readFileSync(baseProfilePath, 'utf8');
      const lines = content.split('\n');
      const pIdx = lines.findIndex(l => l.startsWith('proxies:'));
      const pgIdx = lines.findIndex(l => l.startsWith('proxy-groups:'));
      if (pIdx !== -1 && pgIdx !== -1) {
        transitProxies = lines.slice(pIdx + 1, pgIdx);
      }
    } catch (e) {
      console.error('Error reading base profile:', e);
    }
  }

  // PureISP Residential Proxy Object definition
  const residentialProxyYaml = `    - name: "${egressNode.egressName}"
      type: http
      server: ${egressNode.server}
      port: ${egressNode.port}
      username: "${egressNode.username}"
      password: "${egressNode.password}"
      skip-cert-verify: true
      network: tcp
      dialer-proxy: "${frontNodeName}"`;

  const yamlContent = `mixed-port: 7890
allow-lan: true
mode: rule
log-level: info
unified-delay: true
tcp-concurrent: true

proxies:
${transitProxies.join('\n')}
${residentialProxyYaml}

proxy-groups:
  - name: "🚀 PureISP 住宅出口"
    type: select
    proxies:
      - "${egressNode.egressName}"

  - name: "🤖 OpenAI/ChatGPT 专线直连"
    type: select
    proxies:
      - "♻️ 美国自动选择"
      - "❇️美国-T"
      - "❇️美国-IT"
      - "❇️美国-N"
      - "🇺🇸西美-E(通用)"
      - "🇺🇸西美-F(通用)"

  - name: "♻️ 美国自动选择"
    type: url-test
    url: "http://cp.cloudflare.com/generate_204"
    interval: 300
    proxies:
      - "❇️美国-T"
      - "❇️美国-IT"
      - "❇️美国-N"
      - "🇺🇸西美-E(通用)"
      - "🇺🇸西美-F(通用)"

rules:
  # 住宅出口保护 (Gemini、Claude 等高权重业务严走中继+静态住宅 IP，确保纯净度防封号)
  - DOMAIN-SUFFIX,claude.ai,🚀 PureISP 住宅出口
  - DOMAIN-SUFFIX,anthropic.com,🚀 PureISP 住宅出口
  - DOMAIN-SUFFIX,gemini.google.com,🚀 PureISP 住宅出口

  # OpenAI/ChatGPT 智能旁路 (仅限 ChatGPT 绕过住宅代理直连美国高速 VPS，解决 403 阻断)
  - DOMAIN-SUFFIX,chatgpt.com,🤖 OpenAI/ChatGPT 专线直连
  - DOMAIN-SUFFIX,oaistatic.com,🤖 OpenAI/ChatGPT 专线直连
  - DOMAIN-SUFFIX,oaiusercontent.com,🤖 OpenAI/ChatGPT 专线直连
  - DOMAIN-SUFFIX,openai.com,🤖 OpenAI/ChatGPT 专线直连

  # 国内流量直接直连
  - GEOIP,cn,DIRECT
  - DOMAIN-SUFFIX,cn,DIRECT
  - DOMAIN-KEYWORD,-cn,DIRECT

  # 其余所有出海业务流量走 100% 原生静态住宅 IP 代理链
  - MATCH,🚀 PureISP 住宅出口
`;

  return yamlContent;
}

module.exports = {
  generateClashYaml
};
