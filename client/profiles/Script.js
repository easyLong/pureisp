function main(config) {
  // 1. 定义 Oxylabs 静态住宅 ISP 节点 (链式代理：中继 ➔ 静态住宅 IP)
  const egressUsername = (typeof process !== 'undefined' && process.env && process.env.PUREISP_EGRESS_USERNAME) || 'CONFIGURE_SERVER_SIDE';
  const egressPassword = (typeof process !== 'undefined' && process.env && process.env.PUREISP_EGRESS_PASSWORD) || 'CONFIGURE_SERVER_SIDE';
  const oxylabsStatic = {
    name: "Oxylabs-Static-US(AT&T)",
    type: "http",
    server: "disp.oxylabs.io",
    port: 8001,
    username: egressUsername,
    password: egressPassword,
    "skip-cert-verify": true,
    "network": "tcp",
    "dialer-proxy": "❇️美国-T"
  };

  // 2. 追加 Oxylabs 节点
  config.proxies = config.proxies || [];
  const existingIdx = config.proxies.findIndex(p => p.name === oxylabsStatic.name);
  if (existingIdx !== -1) {
    config.proxies[existingIdx] = oxylabsStatic;
  } else {
    config.proxies.push(oxylabsStatic);
  }

  // 3. 追加到原有代理组并置顶
  config["proxy-groups"] = config["proxy-groups"] || [];
  const targetGroups = ["🚀 节点选择", "手动选择"];
  targetGroups.forEach(gname => {
    const group = config["proxy-groups"].find(g => g.name === gname);
    if (group) {
      group.proxies = group.proxies || [];
      group.proxies = group.proxies.filter(p => p !== oxylabsStatic.name);
      group.proxies.unshift(oxylabsStatic.name);
    }
  });

  // 4. 建立"Oxylabs运营专用"住宅出口分组
  let oxGroup = config["proxy-groups"].find(g => g.name === "Oxylabs运营专用");
  if (!oxGroup) {
    config["proxy-groups"].unshift({
      name: "Oxylabs运营专用",
      type: "select",
      proxies: [oxylabsStatic.name]
    });
  } else {
    oxGroup.proxies = [oxylabsStatic.name];
  }

  // 5. 严格筛选美国机场节点供 ChatGPT 智能旁路使用
  const isUsNode = (name) => {
    return name !== oxylabsStatic.name && (name.includes("美") || name.includes("US") || name.includes("United States"));
  };
  const usProxies = config.proxies.filter(p => isUsNode(p.name)).map(p => p.name);

  // 6. 创建"♻️ 美国自动选择"
  let usAutoGroup = config["proxy-groups"].find(g => g.name === "♻️ 美国自动选择");
  if (usAutoGroup) {
    usAutoGroup.proxies = [...usProxies];
  } else {
    config["proxy-groups"].unshift({
      name: "♻️ 美国自动选择",
      type: "url-test",
      url: "http://cp.cloudflare.com/generate_204",
      interval: 300,
      proxies: [...usProxies]
    });
  }

  // 7. 建立 "🤖 AI 专用通道"（仅针对解决 403 阻断的 ChatGPT）
  const aiProxies = ["♻️ 美国自动选择", ...usProxies];
  let aiGroup = config["proxy-groups"].find(g => g.name === "🤖 AI 专用通道" || g.name === "🤖 OpenAI");
  if (aiGroup) {
    aiGroup.name = "🤖 AI 专用通道";
    aiGroup.proxies = aiProxies;
  } else {
    config["proxy-groups"].unshift({
      name: "🤖 AI 专用通道",
      type: "select",
      proxies: aiProxies
    });
  }

  return config;
}
