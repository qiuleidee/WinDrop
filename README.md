# WinDrop — Windows 右键/复制 ⇄ iOS 快捷指令极简隔空互传工具

**WinDrop** 是一款参考并增强自 [AirDropPlus](https://github.com/yeyt97/AirDropPlus) / [AirDropPlus.Net](https://github.com/qiuleidee/AirDropPlus.Net) 思路的 Windows 原生轻量互传工具：
- **不挑任何电脑网卡**：100% 兼容任意内置无线/有线网卡（含 `Intel(R) Wireless-AC 9462`），零内核驱动、无 WebView2 臃肿依赖。
- **成品官方签名快捷指令开箱即用**：直接提供已通过 Apple iCloud 官方签名的成品快捷指令（扫码点一下「添加」即可，无需手动编写任何动作）。
- **最符合直觉的极简交互**：
  - **Windows → iPhone**：电脑端只需对文件点 **右键「通过 WinDrop 分享」** 或直接 **`Ctrl + C` 复制（文件 / 截图 / 文字）**，iPhone 运行快捷指令点「接收」即可秒收！
  - **iPhone → Windows**：iPhone 在相册/文件打开系统 **「共享表单（Share Sheet）」** 点一下快捷指令，**全自动静默保存到电脑**，右下角通知支持一键「打开文件 / 定位目录 / 复制文件」！

---

## 核心增强亮点（相比原版开源方案）

1. **深度集成 Windows 右键菜单「通过 WinDrop 分享」**：选中单个或多个文件（或整个文件夹）右键一键就绪，与 `Ctrl + C` 复制文件无缝打通。
2. **纯内置 mDNS（`<本机计算机名>.local`）**：无需安装 Apple Bonjour，直接自动读取本机计算机名广播 `.local` 域名，局域网 IP 变动也不影响连接。
3. **纯异步流式大文件传输 + 授权路径白名单**：
   - 上传与下载全程采用 128KB 分块 `FileStream` / `MultipartReader` 流式读写，传输数 GB 的 4K 原片视频内存恒定 < 25MB，绝不发生整文件读入内存导致的崩溃。
   - 严格限制 `/file/{encodedPath}` 仅允许读取经电脑「复制」或「右键分享」显式授权的文件，彻底封堵任意路径读取漏洞。
4. **手机扫码一键引导页（`/setup`）**：扫码即可点击跳转安装 Apple 官方签名快捷指令，并支持一键复制 `host`、`port`、`key` 三项参数。
5. **可选自动将 iPhone HEIC 照片转为 JPG**：调用 Windows 原生图像编码器自动转换。
6. **电脑主动推送唤醒 iPhone 一键接收（升级项 #1）**：支持接入免费开源的 **Bark (APNs)**（含自建服务器）。右键分享文件或按下全局热键 **`Ctrl + Shift + D`** 推送剪贴板时，iPhone 锁屏/顶部横幅立刻弹窗，轻点通知直接拉起快捷指令完成局域网接收！
7. **零菜单拆分版快捷指令引擎（升级项 #2）**：新增 `/v2/send`、`/v2/receive` 与短 Ticket `/v2/dl/{ticket}` 专线接口，自动导出包含 `url / text / image / video / file` 五路智能归档分支的拆分版 `.shortcut` 源码模板与 macOS / GitHub Actions 自动签名工作流（`.github/workflows/sign-shortcuts.yml`）。

---

## 快速使用（30 秒配好）

1. **运行电脑端**：双击根目录下的 [启动WinDrop.bat](启动WinDrop.bat)（或运行 `dist\WinDrop\WinDrop.exe`），在设置窗口点击一次 **「🛡️ 一键放行防火墙」**。
2. **安装成品 iPhone 快捷指令**：
   - 用 iPhone 相机扫描程序窗口左侧二维码（或直接查看 `shortcuts/iPhone扫码安装官方快捷指令.png`，成品 iCloud 链接：`https://www.icloud.com/shortcuts/c499c9a3d9b04e189cce38d9560b3e2e`）。
   - 添加时填入窗口自动显示的 `host`（`<本机计算机名>.local`）、`port`（`58446`）和 `key`。
   - 详细图文说明见：[iPhone 成品快捷指令一键安装与使用指南](docs/IOS_SHORTCUTS_GUIDE.md)。

---

## 工程目录与构建命令

```text
WinDrop/
├── shortcuts/                   # 导出的成品快捷指令扫码安装图片、iCloud 直达链接与模板源码
├── docs/
│   ├── PRD.md                   # 产品需求规格文档
│   └── IOS_SHORTCUTS_GUIDE.md   # 成品快捷指令安装与使用指南
├── src/
│   ├── WinDrop.Core/            # 核心流式 HTTP 服务、双域名 mDNS、路径白名单与沙箱清洗
│   └── WinDrop.App/             # 原生极简托盘程序、文件剪贴板、右键菜单与可交互通知窗
└── tests/
    └── WinDrop.Tests/           # xUnit 单元测试与端到端集成测试
```

```powershell
# 编译与运行全部自动化测试
dotnet build WinDrop.sln -c Release
dotnet test tests/WinDrop.Tests/WinDrop.Tests.csproj -c Release

# 发布生产运行包并导出快捷指令资产包
dotnet publish src/WinDrop.App/WinDrop.App.csproj -c Release -o dist/WinDrop
.\dist\WinDrop\WinDrop.exe --export-shortcuts shortcuts
```

## 许可证
MIT License
