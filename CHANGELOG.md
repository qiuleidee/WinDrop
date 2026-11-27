# WinDrop 更新说明（CHANGELOG）

记录 **WinDrop** 的重要功能演进、网络兼容性增强、界面优化与关键问题修复。

---

## [2026-11-27] 复杂网络联机深度优化与设置窗口重构

### 🌐 复杂网络环境联机优化
针对代理软件 **TUN 模式**、**虚拟网卡**、**系统代理**、**多网卡并发** 与 **公共网络（Public Wi-Fi）** 等易导致 iPhone 与电脑无法互通的场景进行了系统性重构：

- **虚拟网卡与 Fake-IP 智能识别过滤（`NetworkHelper`）**
  - 新增 `ClassifyAdapterAndScore`，自动识别并降权 `Wintun`、`sing-tun`、`Clash`、`Mihomo`、`FlClash`、`v2ray`、`Xray`、`WireGuard`、`Tailscale`、`ZeroTier`、`Radmin`、`OpenVPN`、`Hyper-V`、`WSL`、`Docker`、`VMware`、`VirtualBox` 等虚拟/隧道网卡。
  - 自动过滤 `198.18.0.0/15`（Fake-IP）与 `100.64.0.0/10`（CGNAT）网段，防止配对二维码和 `.local` 解析被假 IP 污染。
  - 对 **Windows 移动热点（Wi-Fi Direct / `192.168.137.x`）** 与有真实网关的物理 Wi-Fi / 有线网卡优先排序。
- **抗 TUN 劫持多网卡 mDNS 引擎（`MdnsResponder`）**
  - 放弃单一 `0.0.0.0:5353` 发包模式，改为为每个真实物理/热点网卡单独绑定对应本地 IP 与 `MulticastInterface` 索引，避免组播被 TUN 低跃点默认路由吞噬。
  - 收到 `.local` 查询时按来访来源 IP 自动匹配同子网本机 IP（`SelectBestIpForRemotePeer`），并同时发送单播 + 组播双应答。
  - 订阅 `NetworkChange` 网络拓扑变化事件（带防抖），切换热点、Wi-Fi 或开关代理后自动刷新绑定并重发广播。
- **首选网卡锁定与一键网络联机自检**
  - `AppConfig` 新增 `PreferredLocalIp`，支持在设置窗口手动锁定指定物理网卡 IP。
  - 新增 `DiagnoseEnvironment` 诊断引擎与窗口「🔍 联机自检」报告，一键检查防火墙规则、Windows 系统代理状态、物理候选 IP 与已过滤虚拟网卡。
  - 新增免鉴权 `GET /ping` 连通性探针，并在 `/setup` 引导页展示全部可用物理/热点备用地址及复杂网络排障指引。
- **Windows 防火墙公用网络拦截图清除（`WindowsSystemIntegrationService`）**
  - 执行防火墙放行时先清理 Windows 首次弹窗残留的程序级 `Block` 规则，再统一写入覆盖专网/域/公网的程序允许规则、TCP 服务端口规则与 UDP 5353 mDNS 规则。
- **系统代理故障自动回退（`BarkPushService`）**
  - 访问局域网/私网地址自动绕过系统代理，公网推送若因失效代理报错则自动切换无代理直连通道重试一次。
- **自动化测试覆盖**
  - 新增 `ComplexNetworkResilienceTests`，覆盖 TUN/Fake-IP/CGNAT 识别、多网卡同子网匹配、代理异常回退与 `/ping` 探针验证。

### 🎨 配对与设置窗口 UI 升级（`PairingAndSettingsWindow`）
根据高 DPI 实际运行显示问题对设置窗口进行了全面美化与自适应重构（详见 [UI_OPTIMIZATION_PROMPT.md](docs/UI_OPTIMIZATION_PROMPT.md)）：

- **深色标题栏与可缩放高 DPI 布局**：接入 `dwmapi.dll` 的 `DwmSetWindowAttribute` 启用原生沉浸式深色标题栏；默认尺寸调整为 `850 × 720`（最小 `800 × 640`，`CanResizeWithGrip`），全局统一 `Microsoft YaHei UI` 字体。
- **统一深色圆角控件体系**：通过 `XamlReader.Parse` 注入标准 WPF 样式，统一按钮、输入框（含聚焦蓝框与 `PART_ContentHost` 内边距）、深色弹窗 `ComboBox` 及支持自动换行的深色 `CheckBox`。
- **左侧扫码配对卡片增强**：新增实时服务状态胶囊（绿色状态圆点、端口与已过滤虚拟/TUN 网卡数量提示）及「🌐 浏览器打开引导页」直达按钮。
- **右侧三段式自适应布局**：固定标题 + 带 `ScrollViewer` 滚动保护的参数区 + 固定底部操作栏；精简网卡下拉文案和「唤醒快捷指令名:」标签（补充提示移入 `ToolTip`），底部三按钮改为「🛡️ 放行防火墙」、「🔍 联机自检」、「💾 保存设置」且预留多行自动换行状态栏，彻底解决文字与提示被截断的问题。

### 📋 手机端纯文本互传与交互修复
- 支持 iPhone 快捷指令读取剪贴板文本并直接发送至电脑；桌面端接收纯文本后直接写入 Windows 剪贴板并弹出轻通知提示，不再保存为磁盘 `.dat` 文件。

### 📚 仓库文档补充
- 新增 [复杂网络环境联机优化方案与修改提示词](docs/NETWORK_OPTIMIZATION_PROMPT.md)
- 新增 [配对与设置窗口 UI 诊断及优化提示词](docs/UI_OPTIMIZATION_PROMPT.md)
