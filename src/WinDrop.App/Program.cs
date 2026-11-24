using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WinDrop.App.Services;
using WinDrop.App.Views;
using WinDrop.Core.Server;
using WinDrop.Core.Services;

namespace WinDrop.App;

/// <summary>
/// WinDrop 极简托盘主程序
/// 支持：右键菜单选文件静默放入分享剪贴板、系统托盘常驻、交互式右下角通知卡片、全局热键推送 iPhone
/// </summary>
public sealed class WinDropApplication : System.Windows.Application
{
    private const int HotKeyIdPushToPhone = 0x5744;
    private const uint ModControlShift = 0x0002 | 0x0004;
    private const uint VkD = 0x44; // Ctrl + Shift + D

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private SingleInstanceIpcService? _ipc;
    private WinDropStateService? _state;
    private WindowsClipboardProvider? _clipboard;
    private WindowsHeicConverter? _heicConverter;
    private WindowsSystemIntegrationService? _sysIntegration;
    private WinDropServer? _server;
    private PairingAndSettingsWindow? _settingsWindow;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private HwndSource? _hotKeySource;
    private Icon? _generatedIcon;

    [STAThread]
    public static int Main(string[] args)
    {
        // 支持一键导出快捷指令二维码与模板资产（供打包构建使用）
        int exportIdx = Array.FindIndex(args, a => string.Equals(a, "--export-shortcuts", StringComparison.OrdinalIgnoreCase));
        if (exportIdx >= 0 && exportIdx + 1 < args.Length)
        {
            ExportShortcutAssets(args[exportIdx + 1]);
            return 0;
        }

        var filesFromArgs = ParseSendArguments(args);
        bool smokeTestMode = args.Any(a => string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase));

        using var ipc = new SingleInstanceIpcService();
        if (!smokeTestMode && !ipc.TryAcquirePrimaryInstance())
        {
            // 已在后台常驻运行：将右键选中的文件通过命名管道转交给主进程写入剪贴板分享槽，然后立即静默退出
            SingleInstanceIpcService.SendToPrimaryInstanceAsync(filesFromArgs).GetAwaiter().GetResult();
            return 0;
        }

        var app = new WinDropApplication();
        app.InitializeRuntime(ipc, args, filesFromArgs, smokeTestMode);
        return app.Run();
    }

    private static void ExportShortcutAssets(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var state = new WinDropStateService();
        string host = $"{System.Net.Dns.GetHostName()}.local";
        int port = state.Config.Port;
        string key = state.Config.Key;

        // 1. 导出 Apple 官方 iCloud 已签名快捷指令扫码安装 PNG
        byte[] qrPng = QrAndPathHelper.GenerateQrPngBytes(QrAndPathHelper.OfficialSignedShortcutUrl, 14);
        File.WriteAllBytes(Path.Combine(outputDir, "iPhone扫码安装官方快捷指令.png"), qrPng);

        // 2. 导出 Windows Internet 快捷方式（双击直接打开 iCloud 快捷指令页）
        string urlShortcutContent = $"[InternetShortcut]\r\nURL={QrAndPathHelper.OfficialSignedShortcutUrl}\r\n";
        File.WriteAllText(Path.Combine(outputDir, "点击获取iOS成品快捷指令(iCloud官方签名).url"), urlShortcutContent);

        // 3. 同时导出拆分版 v2 完整动作流模板与 macOS/CI 官方签名脚本
        File.WriteAllBytes(
            Path.Combine(outputDir, "发送到电脑.shortcut"),
            ShortcutBuilderService.BuildSendToWindowsShortcutPlist(host, port, key));
        File.WriteAllBytes(
            Path.Combine(outputDir, "从电脑接收.shortcut"),
            ShortcutBuilderService.BuildReceiveFromWindowsShortcutPlist(host, port, key));
        File.WriteAllText(
            Path.Combine(outputDir, "sign_on_mac.sh"),
            ShortcutBuilderService.BuildMacSignScript());
    }

    private void InitializeRuntime(SingleInstanceIpcService ipc, string[] args, List<string> initialFiles, bool smokeTestMode)
    {
        _ipc = ipc;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Startup += async (_, _) =>
        {
            _state = new WinDropStateService();
            _clipboard = new WindowsClipboardProvider();
            _heicConverter = new WindowsHeicConverter();
            _sysIntegration = new WindowsSystemIntegrationService();

            _server = new WinDropServer(
                _state,
                clipboard: _clipboard,
                heicConverter: _heicConverter);

            await _server.StartAsync();

            if (smokeTestMode)
            {
                await Task.Delay(300);
                await ShutdownCleanlyAsync();
                return;
            }

            // 根据用户配置自动确保资源管理器右键菜单「通过 WinDrop 分享」就绪
            string? currentExe = Environment.ProcessPath;
            if (_state.Config.EnableContextMenu && !string.IsNullOrEmpty(currentExe) && File.Exists(currentExe))
            {
                _sysIntegration.RegisterContextMenu(currentExe);
            }

            _state.NotificationRaised += NotificationToastWindow.ShowToast;

            SetupTrayIcon();
            SetupGlobalHotKey();

            _ipc.FilesReceivedFromSecondaryInstance += files =>
            {
                Dispatcher.InvokeAsync(async () =>
                {
                    if (_server != null)
                        await _server.ShareFilesFromContextMenuAsync(files);
                });
            };
            _ipc.ActivateMainWindowRequested += () =>
            {
                Dispatcher.InvokeAsync(ShowSettingsWindow);
            };
            _ipc.StartPipeServer();

            if (initialFiles.Count > 0)
            {
                await _server.ShareFilesFromContextMenuAsync(initialFiles);
            }
            else if (!args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)))
            {
                // 直接双击启动时展示简洁的配对与设置小窗，关掉后静默常驻托盘
                ShowSettingsWindow();
            }
        };
    }

    private void SetupTrayIcon()
    {
        if (_server == null || _state == null)
            return;

        _generatedIcon = CreateDynamicAppIcon();
        var menu = new System.Windows.Forms.ContextMenuStrip();

        var showQrItem = new System.Windows.Forms.ToolStripMenuItem("📱 快捷指令二维码与设置");
        showQrItem.Font = new Font(showQrItem.Font, System.Drawing.FontStyle.Bold);
        showQrItem.Click += (_, _) => ShowSettingsWindow();
        menu.Items.Add(showQrItem);

        var pushClipItem = new System.Windows.Forms.ToolStripMenuItem("📲 推送当前剪贴板并通知 iPhone (Ctrl+Shift+D)");
        pushClipItem.Click += async (_, _) =>
        {
            if (_server != null)
                await _server.PushCurrentClipboardToPhoneAsync();
        };
        menu.Items.Add(pushClipItem);

        var openSaveDirItem = new System.Windows.Forms.ToolStripMenuItem("📁 打开接收文件夹");
        openSaveDirItem.Click += (_, _) =>
        {
            Directory.CreateDirectory(_state.Config.SaveDirectory);
            Process.Start("explorer.exe", _state.Config.SaveDirectory);
        };
        menu.Items.Add(openSaveDirItem);

        var copyKeyItem = new System.Windows.Forms.ToolStripMenuItem($"🔑 复制连接密钥 ({_state.Config.Key})");
        _state.ConfigChanged += () => copyKeyItem.Text = $"🔑 复制连接密钥 ({_state.Config.Key})";
        copyKeyItem.Click += (_, _) => System.Windows.Clipboard.SetText(_state.Config.Key);
        menu.Items.Add(copyKeyItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出 WinDrop");
        exitItem.Click += async (_, _) => await ShutdownCleanlyAsync();
        menu.Items.Add(exitItem);

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _generatedIcon,
            Text = $"WinDrop 运行中 ({_server.PrimaryMdnsDomain}:{_server.ListeningPort})",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                ShowSettingsWindow();
            }
        };
    }

    private void SetupGlobalHotKey()
    {
        try
        {
            var parameters = new HwndSourceParameters("WinDropHotKeySink")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0
            };
            _hotKeySource = new HwndSource(parameters);
            _hotKeySource.AddHook(HotKeyWndProc);
            RegisterHotKey(_hotKeySource.Handle, HotKeyIdPushToPhone, ModControlShift, VkD);
        }
        catch { }
    }

    private IntPtr HotKeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmHotkey = 0x0312;
        if (msg == WmHotkey && wParam.ToInt32() == HotKeyIdPushToPhone)
        {
            handled = true;
            if (_server != null)
            {
                _ = _server.PushCurrentClipboardToPhoneAsync();
            }
        }
        return IntPtr.Zero;
    }

    private void ShowSettingsWindow()
    {
        if (_server == null || _sysIntegration == null)
            return;

        _settingsWindow ??= new PairingAndSettingsWindow(_server, _sysIntegration);
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }
        _settingsWindow.Activate();
    }

    private async Task ShutdownCleanlyAsync()
    {
        if (_hotKeySource != null)
        {
            try { UnregisterHotKey(_hotKeySource.Handle, HotKeyIdPushToPhone); } catch { }
            _hotKeySource.Dispose();
            _hotKeySource = null;
        }

        if (_state != null)
        {
            _state.NotificationRaised -= NotificationToastWindow.ShowToast;
        }

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _generatedIcon?.Dispose();

        if (_settingsWindow != null)
        {
            _settingsWindow.AllowTrueClose = true;
            _settingsWindow.Close();
        }

        if (_server != null)
        {
            await _server.DisposeAsync();
            _server = null;
        }

        Shutdown(0);
    }

    private static List<string> ParseSendArguments(string[] args)
    {
        var files = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--send", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                files.Add(args[++i]);
            }
            else if (!args[i].StartsWith("--", StringComparison.Ordinal) && (File.Exists(args[i]) || Directory.Exists(args[i])))
            {
                files.Add(args[i]);
            }
        }
        return files;
    }

    private static Icon CreateDynamicAppIcon()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string icoFile = Path.Combine(baseDir, "Assets", "app.ico");
            if (File.Exists(icoFile))
            {
                return new Icon(icoFile);
            }

            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                var extracted = Icon.ExtractAssociatedIcon(exePath);
                if (extracted != null)
                    return extracted;
            }
        }
        catch { }

        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, 64, 64), Color.FromArgb(37, 99, 235), Color.FromArgb(6, 182, 212), 45f);
            g.FillEllipse(brush, 2, 2, 60, 60);
            using var penOuter = new System.Drawing.Pen(Color.FromArgb(140, 255, 255, 255), 3f);
            using var penInner = new System.Drawing.Pen(Color.FromArgb(220, 255, 255, 255), 3f);
            g.DrawEllipse(penOuter, 12, 12, 40, 40);
            g.DrawEllipse(penInner, 21, 21, 22, 22);
            g.FillEllipse(System.Drawing.Brushes.White, 28, 28, 8, 8);
        }
        IntPtr hIcon = bmp.GetHicon();
        return (Icon)Icon.FromHandle(hIcon).Clone();
    }
}
