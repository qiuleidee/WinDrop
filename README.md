# WinDrop — Windows 右键/复制 ⇄ iOS 快捷指令极简隔空互传工具

**WinDrop** 是一款参考并增强自 [AirDropPlus](https://github.com/yeyt97/AirDropPlus) / [AirDropPlus.Net](https://github.com/qiuleidee/AirDropPlus.Net) 思路的 Windows 原生轻量互传工具：
- **不挑任何电脑网卡**：100% 兼容任意内置无线/有线网卡，零内核驱动、无 WebView2 臃肿依赖。
- **内置 Apple 官方已签名拆分版快捷指令**：仓库 `shortcuts/` 目录直接提供签署完成的发送、接收两个独立快捷指令，iPhone 导入后按窗口显示的本机域名与密钥简单改两处参数即可使用。
- **最符合直觉的极简交互**：
  - **Windows → iPhone**：电脑端只需对文件点 **右键「通过 WinDrop 分享」** 或直接 **`Ctrl + C` 复制（文件 / 截图 / 文字 / 网址）**，iPhone 运行接收快捷指令即可秒收并按格式自动归档！
  - **iPhone → Windows**：iPhone 在相册/文件打开系统 **「共享表单（Share Sheet）」** 点一下发送快捷指令，**多张图片/多个文件全自动循环流式保存到电脑**，右下角通知支持一键「打开文件 / 定位目录 / 复制文件」！

---

## 核心增强亮点（相比原版开源方案）

1. **深度集成 Windows 右键菜单「通过 WinDrop 分享」**：选中单个或多个文件（或整个文件夹）右键一键就绪，与 `Ctrl + C` 复制文件无缝打通。
2. **纯内置 mDNS（`<本机计算机名>.local`）**：无需安装 Apple Bonjour，直接自动读取本机计算机名广播 `.local` 域名，局域网 IP 变动也不影响连接。
3. **纯异步流式大文件传输 + 授权路径白名单**：
   - 上传与下载全程采用 128KB 分块 `FileStream` / `MultipartReader` 流式读写，传输数 GB 的 4K 原片视频内存恒定 < 25MB，绝不发生整文件读入内存导致的崩溃。
   - 严格限制下载接口仅允许读取经电脑「复制」或「右键分享」显式授权的文件，彻底封堵任意路径读取漏洞。
4. **手机扫码引导页（`/setup`）**：iPhone 相机扫码即可一屏复制 `host`、`port`、`key` 以及 `/v2/send`、`/v2/receive` 专线地址。
5. **可选自动将 iPhone HEIC 照片转为 JPG**：调用 Windows 原生图像编码器自动转换。
6. **电脑主动推送唤醒 iPhone 一键接收（升级项 #1）**：支持接入免费开源的 **Bark (APNs)**（含自建服务器）。右键分享文件或按下全局热键 **`Ctrl + Shift + D`** 推送剪贴板时，iPhone 锁屏/顶部横幅立刻弹窗，轻点通知直接拉起快捷指令完成局域网接收！
7. **零菜单拆分版快捷指令引擎（升级项 #2）**：提供 `POST /v2/send`、`GET /v2/receive` 与短凭证 `GET /v2/dl/{ticket}` 专线接口，告别旧版二选一菜单，实现：
   - **发送**：支持共享表单多图/多文件自动循环上传并提示总数量，纯文本发送自动直写电脑剪贴板；
   - **接收**：按内容类型四路自动分流（照片/视频存系统相册、文档静默存「文件」App、网址直开 Safari、纯文本入手机剪贴板）。
8. **复杂网络自适应与一键联机自检**：自动过滤 TUN / Fake-IP（`198.18.0.0/15`）/ 各类虚拟网卡并优先生效物理网卡或电脑移动热点；内置多网卡独立 mDNS 绑定、系统代理异常直连回退与防火墙拦截规则自动清理。
9. **现代深色圆角原生设置界面**：支持 Windows 深色标题栏、可缩放高 DPI 滚动布局、实时服务状态胶囊，以及一键「浏览器打开引导页」与「联机自检」。

---

## 已签名拆分版快捷指令与快速使用（1 分钟配好）

仓库 `shortcuts/` 目录下已为你准备好可直接导入 iPhone 的两份 Apple 官方已签名文件：
- **发送专用**：[WinDrop发送_已签名.shortcut](shortcuts/WinDrop发送_已签名.shortcut)（共享表单多图/多文件自动循环发到电脑）
- **接收专用**：[WinDrop接收_已签名.shortcut](shortcuts/WinDrop接收_已签名.shortcut)（零菜单一键从电脑接收并四路智能归档）

### 首次配置步骤
1. **运行电脑端**：双击根目录下的 [启动WinDrop.bat](启动WinDrop.bat)（或 `dist\WinDrop\WinDrop.exe`），在设置窗口点击一次 **「🛡️ 放行防火墙」**（如处于多网卡或代理 TUN 环境，可点击 **「🔍 联机自检」** 或锁定首选物理网卡 IP），记下窗口显示的 **`<本机计算机名>.local`**、**端口（默认 `58446`）** 与 **密钥**。
2. **iPhone 导入并填入本机参数**：
   - 将仓库中的 `WinDrop发送_已签名.shortcut` 和 `WinDrop接收_已签名.shortcut` 发送到 iPhone 并添加到「快捷指令」；
   - 编辑 **「发送」** 快捷指令：把请求 URL 改为 `http://<你的计算机名>.local:58446/v2/send`，把头部 `Authorization` 改为你的密钥；
   - 编辑 **「接收」** 快捷指令：把第 1 步请求 URL 改为 `http://<你的计算机名>.local:58446/v2/receive`，把头部 `Authorization` 改为你的密钥（或直接在电脑 `/setup` 扫码页一键复制两行接口地址粘贴进去）。
3. **必开设置与静默接收技巧**：
   - 打开 iPhone **「设置 → App → 快捷指令 → 高级」**，开启 **「允许共享大量数据」**（防止一次发多张原图时系统拦截）；
   - 如希望接收时**不切进快捷指令主界面、完全静默后台运行**，请在 **「设置 → 辅助功能 → 触控 → 轻点背面」** 或 **控制中心** 绑定 `WinDrop接收`。
   - 详细图文说明与从零自建教程见：[iPhone 快捷指令安装与使用指南](docs/IOS_SHORTCUTS_GUIDE.md)。

---

## 工程目录与构建命令

```text
WinDrop/
├── CHANGELOG.md                       # 版本更新说明
├── shortcuts/
│   ├── WinDrop发送_已签名.shortcut    # Apple 已签名发送快捷指令（共享表单多文件直发）
│   ├── WinDrop接收_已签名.shortcut    # Apple 已签名接收快捷指令（四路自动智能归档）
│   ├── 发送到电脑.shortcut             # 未签名脱敏 XML 模板源码
│   ├── 从电脑接收.shortcut             # 未签名脱敏 XML 模板源码
│   └── sign_on_mac.sh                 # macOS 本地签名辅助脚本
├── docs/
│   ├── PRD.md                         # 产品需求规格文档
│   ├── UPGRADE_ROADMAP.md             # 进阶优化与升级路线图
│   ├── IOS_SHORTCUTS_GUIDE.md         # 快捷指令安装与使用指南
│   ├── NETWORK_OPTIMIZATION_PROMPT.md # 复杂网络联机优化方案与提示词
│   └── UI_OPTIMIZATION_PROMPT.md      # 设置窗口 UI 诊断与重构提示词
├── src/
│   ├── WinDrop.Core/                  # 核心流式 HTTP 服务、mDNS、路径白名单与沙箱清洗
│   └── WinDrop.App/                   # 原生极简托盘程序、文件剪贴板、右键菜单与轻通知窗
└── tests/
    └── WinDrop.Tests/                 # xUnit 单元测试与端到端集成测试
```

```powershell
# 编译与运行全部自动化测试
dotnet build WinDrop.sln -c Release
dotnet test tests/WinDrop.Tests/WinDrop.Tests.csproj -c Release

# 发布生产运行包并导出脱敏后的公开快捷指令模板
dotnet publish src/WinDrop.App/WinDrop.App.csproj -c Release -o dist/WinDrop
.\dist\WinDrop\WinDrop.exe --export-shortcuts shortcuts --template
```

## 更新说明

完整更新记录请参阅：[CHANGELOG.md](CHANGELOG.md)。

## 许可证
MIT License
