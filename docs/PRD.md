# WinDrop — Windows 右键/复制 ⇄ iOS 快捷指令隔空互通需求文档（PRD v2.1）

| 文档版本 | 更新日期 | 核心参考架构 | 开源协议 | 目标平台 |
| :--- | :--- | :--- | :--- | :--- |
| v2.1 | 2026-11-24 | AirDropPlus.Net 增强版（原生托盘 + 官方签名快捷指令） | MIT | Windows 10/11（任意网卡） ↔ iOS / iPadOS（成品快捷指令） |

---

## 1. 核心产品定位与交互准则

1. **极简无打扰**：Windows 端平时静默驻留系统托盘，去除繁琐的网页雷达大控制台，无需额外购买任何特定网卡硬件。
2. **已签名拆分版快捷指令开箱即用**：仓库直接提供经 Apple 官方签名的「发送 / 接收」拆分版快捷指令（`shortcuts/WinDrop发送_已签名.shortcut` 与 `shortcuts/WinDrop接收_已签名.shortcut`）及本机扫码一键复制引导页（`/setup`），支持 `/v2/send`、`/v2/receive` 与 `/v2/dl/{ticket}` 专线接口，同时保留旧版 v1 路由兼容。
3. **双向自然交互**：
   - **Windows → iOS**：用户在电脑上通过 **资源管理器右键菜单「通过 WinDrop 分享」** 或直接 **`Ctrl + C` 复制（单文件/多文件/截图/文字）**，在 iPhone 上触发快捷指令点「接收（Receive）」即可自动拉取保存。
   - **iOS → Windows**：用户在 iPhone 相册/文件/Safari 打开系统 **「共享表单（Share Sheet）」** 选择该快捷指令，无需电脑端弹窗阻塞确认，**全自动流式落盘到接收文件夹**（文字自动同步至电脑剪贴板），并在电脑右下角弹出带「打开文件 / 定位目录 / 复制文件」的可交互轻通知。

---

## 2. 核心接口契约（100% 兼容成品快捷指令）

所有业务接口通过请求头 `Authorization: <key>`（或 `?key=<key>`）校验访问密钥：

| 路由 | 方法 | 请求内容 | 响应格式与行为 |
| :--- | :--- | :--- | :--- |
| `/` | `GET` | 免鉴权 | 返回纯文本 `Hello World!`，并在桌面右下角提示连接测试成功 |
| `/setup` | `GET` | 免鉴权 | 手机扫码引导页：提供一键复制 `host / port / key` 与 `/v2/send`、`/v2/receive` 接口地址 |
| `/file` & `/upload` | `POST` | `multipart/form-data` (`file`) | 纯流式保存至 `SaveDirectory`（自动防重名与安全清洗），返回 `{"success":true,"msg":"发送成功","data":null}` |
| `/clipboard` | `GET` | 无 | 按优先级检测电脑已分享/复制内容并返回：`type="file"`（Base64 路径数组）、`type="img"`（PNG Base64）或 `type="text"` |
| `/clipboard` | `POST` | Form (`clipboard`) 或 JSON | 写入 Windows 系统剪贴板，返回 `{"success":true,"msg":"发送成功","data":null}` |
| `/file/{**encodedPath}` | `GET` | Base64 编码路径 | 校验「已复制/已右键分享路径白名单」后，通过异步 `FileStream` 流式下发文件 |

---

## 3. 相比开源原版的关键工程增强

1. **右键菜单与系统剪贴板合一**：注册 `HKCU\Software\Classes\*\shell\WinDrop` 与文件夹右键「通过 WinDrop 分享」，触发后通过单实例命名管道通知后台主进程将选中文件写入系统 `FileDropList` 并登记下载白名单。
2. **纯内置 mDNS（UDP 5353）**：自动读取本机真实计算机名（`<本机计算机名>.local`）广播与响应 A 记录，无需安装 Apple Bonjour。
3. **大文件零内存溢出与安全沙箱**：修复原版 `File.ReadAllBytesAsync` 传大视频内存溢出问题，全链路使用 128KB 异步流；增加已授权分享文件白名单与路径穿越/保留字清洗，杜绝任意本地文件泄露风险。
