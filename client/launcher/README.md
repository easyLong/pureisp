# Windows 启动器

`PureISPLauncher.cs`、`SystemProxy.cs` 和 `IdeLocator.cs` 是 B/S 极简客户端 1.0.4 的启动器源码，使用 Windows 自带 .NET Framework C# 编译器构建。

```powershell
# 默认生成独立 1.0.4 发行目录中的启动器。
./client/launcher/build.ps1
# 也可以指定独立输出路径。
./client/launcher/build.ps1 -OutputPath ./dist/PureISP-BS-v1.0.4-Win64/PureISP-BS-Portable/PureISP.exe
./client/launcher/test.ps1
# 完整复制内核、配置和 UI 并打包 ZIP；保留旧版本。
./client/launcher/release.ps1
```

## 1.0.4 修复

- ✕ 和最小化仅隐藏到托盘；双击图标或托盘菜单打开面板。面板与托盘“停止并退出”执行真正退出。
- 关机查询 `WM_QUERYENDSESSION` 立即允许；`WM_ENDSESSION` 确认后恢复代理再停止自己的内核，取消关机则保持运行。正常关机不等待内核退出，不弹阻塞对话框；恢复失败保留备份并记录本地日志，后续启动恢复。不承诺固定恢复时间或强制结束/断电下的绝对保证。
- 窗口、托盘、exe 使用统一的双轨分流图标。`assets/pureisp.svg` 为矢量稿，`IconBuilder.cs` 生成 PNG 和含 16/20/24/32/48/64/128/256 八种尺寸的 ICO；运行 `build-icon.ps1` 重新生成，构建时嵌入 exe。
- IDE 路径优先使用有效的手动选择；否则只读查询 HKCU/HKLM App Paths、开始菜单/桌面快捷方式和常见安装目录，不固定盘符。未找到时选择 exe，可随时更换。路径保存在 `%LOCALAPPDATA%/PureISP/ide-path.txt`，不随发行包交付；失效路径重新发现。
- 内核就绪后通过 WinINet 自动接入系统代理，先保存原 flags、地址、绕过和 PAC，退出恢复；不直接写系统代理注册表，不写或删除用户环境变量。
- 从面板启动 Antigravity IDE 时，以 `UseShellExecute=false` 显式传入子进程 `HTTP_PROXY`、`HTTPS_PROXY`、`ALL_PROXY= http://127.0.0.1:7890`，同时配置 Chromium 代理参数。
- 子进程 `NO_PROXY` 保留继承的排除项，补充 localhost、127.0.0.1、::1，供登录回调使用。
- IDE 已经运行时提示用户先保存并退出，避免 Electron 复用携带旧代理的进程。
- 等待内核代理端口和控制器就绪；遇到端口占用报错，不按进程名关闭其他实例。
- 停止并退出时只结束本启动器创建的内核；关闭面板不停止代理。
- 备份位于 `%LOCALAPPDATA%/PureISP/system-proxy.backup`；异常退出后下次启动恢复。如果其他代理软件后来更改设置，不覆盖该修改。

## 使用边界

1. 普通浏览器通过系统代理自动接入同一套规则。IDE 从面板点击“启动 Antigravity IDE（独立代理）”，避免继承旧端口。
2. 保持 PureISP 运行，IDE 才能继续使用 7890；停止后，该 IDE 会失去其代理。关闭 IDE 后使用普通快捷方式重新启动，可恢复普通启动环境。
3. 修复版恢复本次启动前的系统配置，不猜测更早已被旧版删除的用户环境变量。旧版应先“停止并退出”，再启动修复版。
4. 代理端口和控制端口约定为 7890 / 9090；修改发行包配置时须同步启动器。

## 验证

回归测试验证旧 7897 不会被 IDE 继承；以模拟设置存储测试原代理和 PAC 恢复、第三方接管保留、失败回滚及异常恢复；使用临时配置和随机端口测试真实内核。测试本身不改实际系统代理，前后比较确认用户环境不变。

路径测试覆盖含空格的安装目录、选择持久化、手动更换、失效路径回退、无安装时请求选择以及真实 Windows 快捷方式解析。测试快捷方式和假 exe 仅生成在临时目录，假 exe 不执行。

生命周期测试在随机端口的真实内核上验证 ✕ 隐藏、托盘打开、最小化、恢复失败保持内核和备份、重试退出及恢复顺序。通过向测试窗口的处理函数传入模拟关机消息验证确认/取消及失败日志；不执行实际关机。测试使用临时目录与模拟代理存储，不修改真实系统代理。

系统设置使用 [Windows 官方 WinINet 接口](https://learn.microsoft.com/en-us/windows/win32/wininet/setting-and-retrieving-internet-options)。Windows 会持久化系统代理配置，使用官方 API 不等于完全不影响注册表；关键是备份恢复系统设置，并避免永久覆盖用户环境变量。

## 1.1.3 卡密版

`AccountClient.cs` 是独立卡密入口，界面填卡密并点击“启动”，无需注册或账号密码。共用原 Core、IDE 路径和安全恢复，分流配置沿用原文件。使用 `build-account.ps1`、`test-account.ps1`、`release-account.ps1` 构建、验证和打包；部署见 [授权服务说明](../../commercial/README.md)。输出到独立 1.1.3 更新目录，保留历史版本。
