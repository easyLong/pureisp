# PureISP 运行与部署

## Node 服务

当前 package.json 不含第三方依赖，只需 Node.js。本机验证环境为 Node.js 24.14.0 / npm 11.9.0。

```powershell
cd C:\Code\pureisp\server
npm start
```

自定义端口：

```powershell
$env:PORT = '3601'
node src/server.js
```

Linux 可在 server 目录执行 `PORT=3601 node src/server.js`。仓库没有 Dockerfile、PM2 配置或 `server/scripts/start.sh`；根目录 BAT 是 Windows 入口。

打开 `/health` 验证 HTTP 服务，再打开官网 `/` 或控制台 `/console`。状态页面含演示数据，不表示代理已连接。Ctrl+C 停止前台 Node 服务。

## 动态订阅前提

1. 检查 `licenses.js` 的出口配置是否适用于部署；两张卡密是内置数据。
2. 生成器依赖 `%APPDATA%/io.github.clash-verge-rev.clash-verge-rev/profiles/LC8RvVCmlJoz.yaml`；未设置 APPDATA 时回退到 Windows Administrator 固定路径。
3. 原订阅须含代理组引用的节点，否则 HTTP 返回 200 后，内核仍可能拒绝配置。
4. 部署机器上先生成配置并用目标 Mihomo 校验，再交付。接口返回成功不等于配置完整。

生成器尚无可移植基础配置入口或缺失依赖错误返回。Linux / 云服务器部署须先完成这些改造。

## Windows 客户端

使用 `dist/PureISP-BS-v1.0.4-Win64/PureISP-BS-Portable/PureISP.exe`。它自动启动包内内核，使用代理 7890 和控制器 9090，不依赖 Node。

新版本备份并接入系统代理，普通浏览器自动使用规则；退出恢复原配置。IDE 从面板启动时获得独立进程代理，不写用户环境变量，见 [客户端指南](CLIENT_GUIDE.md)。端口占用时不强制停止其他客户端。

## 公网部署缺口

代码监听 `0.0.0.0`，API 允许任意来源，控制接口缺少独立鉴权，状态未按用户隔离。现有本地演示服务尚不能直接作为完整商用控制面。

先落实 [待办](PROJECT_STATUS.md) 中的鉴权、真实状态、卡密有效期、配置完整性、凭据外置和公共订阅 URL，再配置 HTTPS、访问限制、进程守护、日志及备份。

数据库、支付回调、流量计费、后台发卡及多用户授权均未实现。原文档里的 SQLite / Webhook 属于设计方向。

## 排查

| 现象 | 检查项 |
| --- | --- |
| `EADDRINUSE` | 3600 已占用；不要重复运行两个 BAT |
| 下载 404 | 检查 server.js 指向的 ZIP；只复制 server 不会带上 dist |
| YAML 导入失败 | 核对基础订阅及所有节点、组、dialer-proxy 引用 |
| 网页在线，实际断网 | 区分演示状态和内核，测试真实目标请求 |
| IDE 连接 7897 被拒绝 | 完整退出 IDE，改从 1.0.4 面板启动 |
| 远程订阅指向 localhost | 当前公共 URL 配置尚待实现 |
