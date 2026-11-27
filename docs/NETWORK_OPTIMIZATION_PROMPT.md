# WinDrop 复杂网络环境联机优化方案与修改提示词

本文档总结了 WinDrop 在**虚拟网卡、代理软件 TUN 模式、系统代理、多网卡并发、公共网络（Public Wi-Fi）**等复杂网络环境下无法联机的根本原因、系统性解决方案，以及可用于复现或迭代该改造的完整**修改提示词（Prompt）**。

---

## 一、深度思考与根因拆解

### 1. 代理软件 TUN 模式与新型虚拟网卡（Clash / Mihomo / sing-box / Tailscale 等）
- **虚假网卡类型伪装**：`Wintun`、`sing-tun`、`Clash/Mihomo`、`WireGuard`、`Tailscale`、`ZeroTier`、`Radmin` 等虚拟网卡在 Windows NDIS 中往往上报为普通 `Ethernet`（而非 `Tunnel`），并且常分配 `198.18.0.0/15`（Fake-IP）或 `100.64.0.0/10`（CGNAT）。这会导致旧版逻辑将其误判为高优先级有线网卡，污染配对二维码与 `.local` 解析结果。
- **路由表低跃点劫持组播流量**：开启 TUN 后，系统默认路由（`0.0.0.0/0`）优先走 TUN 网卡。旧版 `MdnsResponder` 仅绑定单个通配 `0.0.0.0:5353` 套接字发包，Windows 会将 `224.0.0.251:5353` mDNS 广播/应答包误送入 TUN 隧道黑洞，导致真实局域网中的 iPhone 收不到 `.local` 应答。

### 2. 多网卡并存（有线 + Wi-Fi + 移动热点）与子网错配
- 当电脑同时存在公司内网有线网卡、Wi-Fi、移动热点时，旧版仅返回单一全局主 IP，且移动热点优先级（95）低于处于连通状态的物理 Wi-Fi（100）。即使用户为了绕开 AP 隔离开启了电脑移动热点，iPhone 通过热点查询 `.local` 仍可能拿到不通的外网 Wi-Fi IP。

### 3. 公共网络（Public Wi-Fi）三重阻断
- **Windows 防火墙程序级 `Block` 规则压制**：首次运行时如果系统防火墙弹窗未勾选「公用网络」，Windows 会为 `WinDrop.exe` 自动生成一条公用网络 `Block` 入站规则。在 Windows 防火墙中程序级 `Block` 优先级高于端口 `Allow`，导致只放行 TCP/UDP 端口在公共 Wi-Fi 下依然不通。
- **组播抑制（Multicast Drop）**：大量公共 Wi-Fi/企业 AP 会丢弃 `224.0.0.251` 组播响应包。
- **AP 客户端隔离**：酒店、校园网、咖啡厅 Wi-Fi 开启设备隔离后，同一 Wi-Fi 下手机与电脑根本无法互通，唯一稳定解法是开启电脑移动热点或手机个人热点直连。

### 4. 系统代理与手机端代理规则干扰
- **电脑端死代理 / 全局代理**：电脑端配置了失效系统代理或代理规则未排除私网时，桌面端通过 `HttpClient` 请求私有局域网或官方 Bark 推送可能超时失败。
- **iPhone 端代理接管 `.local`**：手机开启全局代理 / Fake-IP 时，`.local` 域名容易被手机端 VPN DNS 劫持返回虚假 IP，此时必须引导直接使用当前局域网 IP（或提示为 `.local` 放行直连）。

---

## 二、核心修改提示词（可直接复用）

> 以下提示词完整描述本次改造目标、模块分工与兼容性约束，可直接用于后续维护或交由 AI 执行：

```text
请在不破坏 WinDrop 现有全部 v1/v2 接口（/、/setup、/file、/upload、/clipboard、/v2/send、/v2/receive、/v2/dl/{ticket}）、托盘交互、右键分享、Bark 推送和 HEIC 转换功能的前提下，对复杂网络环境（虚拟网卡、代理软件 TUN 模式、系统代理、多网卡、公共网络）下的联机稳定性进行深度优化：

1. 配置扩展（WinDrop.Core/Models/WinDropModels.cs）：
   - 在 AppConfig 中新增 PreferredLocalIp（默认空字符串，表示自动智能优选），支持用户在多网卡环境下手动锁定局域网网卡 IP。

2. 多信号网卡分类与子网优选（WinDrop.Core/Network/NetworkHelper.cs）：
   - 在 LocalNetworkEndpoint 中补充子网掩码（SubnetMask）、接口索引（InterfaceIndex）、是否虚拟/隧道网卡（IsVirtualOrTunnel）与中文分类标签（CategoryLabel）。
   - 提供纯函数 ClassifyAdapterAndScore 用于严格识别 Wintun、sing-tun、SingBox、Clash、Mihomo、FlClash、v2ray、Xray、WireGuard、Tailscale、ZeroTier、Radmin、OpenVPN、Hyper-V、WSL、Docker、VMware、VirtualBox 等虚拟/TUN 网卡以及 198.18.0.0/15（Fake-IP）、100.64.0.0/10（CGNAT）网段，将其压降至最低优先级（<= 10）并标记为虚拟/隧道网卡；仅对 Windows 原生移动热点（Wi-Fi Direct / 192.168.137.x）豁免降权并置顶高优（140），有真实有效默认网关的物理 Wi-Fi（125）和物理有线（115）次之。
   - 实现 SelectBestIpForRemotePeer：根据查询来源 IP 与各物理网卡子网掩码精准匹配同网段本机 IP。
   - 实现 IsPrivateOrLocalHost 与 DiagnoseEnvironment，用于识别 TUN/虚拟网卡和系统代理风险并输出简体中文诊断建议。

3. mDNS 抗 TUN 劫持与抗公共网络优化（WinDrop.Core/Network/MdnsResponder.cs）：
   - 放弃仅绑定单一 0.0.0.0:5353 的发包方式：改为针对每一个非虚拟/非 TUN 的真实物理网卡（及移动热点网卡）分别创建绑定到对应本机 IP:5353 的 UdpClient，并显式设置 SocketOptionName.MulticastInterface 为该网卡 InterfaceIndex，防止组播响应被 TUN 低跃点路由吞掉。
   - 收到 mDNS 查询时，根据来源 IP 子网匹配响应 A 记录，并执行「向查询端 IP:Port 发单播应答 + 通过对应物理网卡发组播应答」双发策略，提升公共 Wi-Fi 下的发现成功率。
   - 订阅 NetworkChange.NetworkAddressChanged 与 NetworkAvailabilityChanged 事件（带防抖），网卡切换或开关热点/代理时自动热重载并重发广播。

4. 系统代理容错（WinDrop.Core/Services/BarkPushService.cs）：
   - 保留默认客户端的同时内置 SocketsHttpHandler { UseProxy = false } 无代理直连客户端；访问局域网/私网地址直接走直连，访问公网地址若因死代理/代理异常失败，自动回退无代理直连通道重试一次。

5. Windows 防火墙彻底放行与可视化自检（WinDrop.App / WinDropServer.cs）：
   - 在 EnsureFirewallRuleAsync 中先清理系统首次弹窗可能残留的程序级阻止规则（netsh advfirewall firewall delete rule name=all program="<exePath>"），再同时创建「程序级入站允许规则 + TCP 端口允许规则 + UDP 5353 允许规则」（覆盖专用/域/公用配置文件）。
   - 在 WinDropServer 中新增免鉴权 GET /ping 探针，并在 /setup 页面列出全部可用物理/热点备用 IP 及复杂网络（代理/公共 Wi-Fi/热点）直连指引。
   - 在 PairingAndSettingsWindow 增加「网卡 / IP 绑定」下拉选择器、「复制 IP」按钮与「🔍 网络联机自检」诊断弹窗，保存设置后立即刷新二维码、绑定选择与 mDNS 广播。

6. 自动化测试验证：
   - 保留现有 34+ 测试全部通过，并新增复杂网络单元测试覆盖：TUN/Fake-IP/CGNAT 识别过滤、同子网多网卡匹配、BarkPushService 代理故障自动直连回退与 /ping 探针。
```
