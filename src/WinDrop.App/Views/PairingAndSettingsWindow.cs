using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinDrop.App.Services;
using WinDrop.Core.Server;
using WinDrop.Core.Services;

namespace WinDrop.App.Views;

/// <summary>
/// 极简原生 WPF「快捷指令安装与连接设置」窗口
/// 无 WebView2 依赖，秒开低内存，扫码直接安装成品签名快捷指令并展示 host / port / key
/// </summary>
public sealed class PairingAndSettingsWindow : Window
{
    private readonly WinDropServer _server;
    private readonly WindowsSystemIntegrationService _sysIntegration;

    private readonly System.Windows.Controls.Image _qrImage;
    private readonly TextBlock _qrCaption;
    private bool _showingDirectIcloudQr;

    private readonly System.Windows.Controls.TextBox _portBox;
    private readonly System.Windows.Controls.TextBox _keyBox;
    private readonly System.Windows.Controls.TextBox _saveDirBox;
    private readonly System.Windows.Controls.CheckBox _ctxMenuCheck;
    private readonly System.Windows.Controls.CheckBox _autoStartCheck;
    private readonly System.Windows.Controls.CheckBox _heicCheck;
    private readonly System.Windows.Controls.CheckBox _pushEnableCheck;
    private readonly System.Windows.Controls.CheckBox _pushIncludeTextCheck;
    private readonly System.Windows.Controls.TextBox _barkInputBox;
    private readonly System.Windows.Controls.TextBox _shortcutNameBox;
    private readonly TextBlock _statusLabel;

    public bool AllowTrueClose { get; set; }

    public PairingAndSettingsWindow(WinDropServer server, WindowsSystemIntegrationService sysIntegration)
    {
        _server = server;
        _sysIntegration = sysIntegration;

        Title = "WinDrop — iPhone 快捷指令配对与设置";
        Width = 700;
        Height = 620;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42));
        try
        {
            string icoFile = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(icoFile))
            {
                Icon = BitmapFrame.Create(new Uri(icoFile, UriKind.Absolute));
            }
        }
        catch { }

        var rootGrid = new Grid { Margin = new Thickness(20) };
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(255) });
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // === 左侧：成品快捷指令安装二维码卡片 ===
        var leftCard = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16)
        };
        var leftStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        leftStack.Children.Add(new TextBlock
        {
            Text = "📱 iPhone 扫码安装快捷指令",
            Foreground = System.Windows.Media.Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 15,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        });

        _qrImage = new System.Windows.Controls.Image { Width = 186, Height = 186 };
        var qrWrap = new Border
        {
            Background = System.Windows.Media.Brushes.White,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 12, 0, 10),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Child = _qrImage
        };
        leftStack.Children.Add(qrWrap);

        _qrCaption = new TextBlock
        {
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            LineHeight = 18,
            Margin = new Thickness(0, 0, 0, 12)
        };
        leftStack.Children.Add(_qrCaption);

        var switchQrBtn = CreateButton("切换：扫 Apple iCloud 直装码", System.Windows.Media.Color.FromRgb(51, 65, 85));
        switchQrBtn.Click += (_, _) =>
        {
            _showingDirectIcloudQr = !_showingDirectIcloudQr;
            switchQrBtn.Content = _showingDirectIcloudQr ? "切换：扫本机引导页二维码" : "切换：扫 Apple iCloud 直装码";
            RefreshQrDisplay();
        };
        leftStack.Children.Add(switchQrBtn);

        leftCard.Child = leftStack;
        Grid.SetColumn(leftCard, 0);
        rootGrid.Children.Add(leftCard);

        // === 右侧：连接参数与系统设置 ===
        var rightCard = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18)
        };
        var rightStack = new StackPanel();

        rightStack.Children.Add(new TextBlock
        {
            Text = "⚙️ 快捷指令连接参数与系统集成",
            Foreground = System.Windows.Media.Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 12)
        });

        // Host 展示行
        string hostText = $"{_server.PrimaryMdnsDomain}  (备用 IP: {_server.LocalIp})";
        rightStack.Children.Add(BuildReadOnlyCopyRow("主机 (host):", hostText, _server.PrimaryMdnsDomain));

        // 端口与密钥行
        var portKeyGrid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        portKeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        portKeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        portKeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });

        _portBox = CreateTextBox(_server.ListeningPort.ToString());
        var portPanel = BuildLabeledInput("端口 (port):", _portBox);
        Grid.SetColumn(portPanel, 0);
        portKeyGrid.Children.Add(portPanel);

        _keyBox = CreateTextBox(_server.State.Config.Key);
        var keyPanel = BuildLabeledInput("密钥 (key):", _keyBox);
        Grid.SetColumn(keyPanel, 2);
        portKeyGrid.Children.Add(keyPanel);

        rightStack.Children.Add(portKeyGrid);

        // 保存路径行
        rightStack.Children.Add(new TextBlock
        {
            Text = "接收文件保存目录:",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 4)
        });
        var dirGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _saveDirBox = CreateTextBox(_server.State.Config.SaveDirectory);
        Grid.SetColumn(_saveDirBox, 0);
        dirGrid.Children.Add(_saveDirBox);

        var browseBtn = CreateButton("浏览…", System.Windows.Media.Color.FromRgb(51, 65, 85));
        browseBtn.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                SelectedPath = _saveDirBox.Text,
                Description = "选择接收 iPhone 分享文件的保存文件夹"
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _saveDirBox.Text = dlg.SelectedPath;
            }
        };
        Grid.SetColumn(browseBtn, 2);
        dirGrid.Children.Add(browseBtn);

        var openDirBtn = CreateButton("打开", System.Windows.Media.Color.FromRgb(51, 65, 85));
        openDirBtn.Click += (_, _) =>
        {
            Directory.CreateDirectory(_saveDirBox.Text);
            Process.Start("explorer.exe", _saveDirBox.Text);
        };
        Grid.SetColumn(openDirBtn, 4);
        dirGrid.Children.Add(openDirBtn);
        rightStack.Children.Add(dirGrid);

        // 复选框区域
        _ctxMenuCheck = CreateCheckBox("启用资源管理器右键「通过 WinDrop 分享」菜单", _sysIntegration.IsContextMenuRegistered());
        _autoStartCheck = CreateCheckBox("开机自动启动并最小化驻留系统托盘", _server.State.Config.AutoStart);
        _heicCheck = CreateCheckBox("收到 iPhone 的 HEIC 照片时自动转换为 JPG", _server.State.Config.ConvertHeicToJpg);

        rightStack.Children.Add(_ctxMenuCheck);
        rightStack.Children.Add(_autoStartCheck);
        rightStack.Children.Add(_heicCheck);

        // === iPhone 主动推送唤醒区域（升级项 #1：Bark APNs 一键弹窗接收） ===
        var pushCard = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 8, 0, 2)
        };
        var pushStack = new StackPanel();
        pushStack.Children.Add(new TextBlock
        {
            Text = "🔔 iPhone 主动推送唤醒（可选 · 配合免费 Bark App）",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248)),
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 6)
        });

        pushStack.Children.Add(new TextBlock
        {
            Text = "Bark Key 或地址（填纯 Key 或复制的链接，留空则不推送）:",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var barkGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        barkGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        barkGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        barkGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _barkInputBox = CreateTextBox(_server.State.Config.BarkKeyOrUrl);
        Grid.SetColumn(_barkInputBox, 0);
        barkGrid.Children.Add(_barkInputBox);

        var testPushBtn = CreateButton("🔔 测试推送", System.Windows.Media.Color.FromRgb(14, 116, 144));
        testPushBtn.Click += async (_, _) => await TestPushAsync();
        Grid.SetColumn(testPushBtn, 2);
        barkGrid.Children.Add(testPushBtn);
        pushStack.Children.Add(barkGrid);

        var scNameRow = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        scNameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scNameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        scNameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var scLbl = new TextBlock
        {
            Text = "唤醒快捷指令名(拆分版填 从电脑接收):",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(scLbl, 0);
        scNameRow.Children.Add(scLbl);
        _shortcutNameBox = CreateTextBox(_server.State.Config.ReceiveShortcutName);
        Grid.SetColumn(_shortcutNameBox, 2);
        scNameRow.Children.Add(_shortcutNameBox);
        pushStack.Children.Add(scNameRow);

        _pushEnableCheck = CreateCheckBox("启用右键分享与热键 (Ctrl+Shift+D) 主动推送通知", _server.State.Config.EnablePushNotification);
        _pushIncludeTextCheck = CreateCheckBox("推送文本/网址时附带内容（支持下拉一键复制或直开网页）", _server.State.Config.PushIncludeClipboardText);
        pushStack.Children.Add(_pushEnableCheck);
        pushStack.Children.Add(_pushIncludeTextCheck);

        pushCard.Child = pushStack;
        rightStack.Children.Add(pushCard);

        // 底部操作按钮行
        var actionGrid = new Grid { Margin = new Thickness(0, 14, 0, 6) };
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var fwBtn = CreateButton("🛡️ 一键放行防火墙", System.Windows.Media.Color.FromRgb(51, 65, 85));
        fwBtn.Click += async (_, _) =>
        {
            var (_, msg) = await _sysIntegration.EnsureFirewallRuleAsync(_server.ListeningPort);
            _statusLabel!.Text = msg;
        };
        Grid.SetColumn(fwBtn, 0);
        actionGrid.Children.Add(fwBtn);

        var saveBtn = CreateButton("💾 保存设置", System.Windows.Media.Color.FromRgb(37, 99, 235));
        saveBtn.Click += (_, _) => SaveAllSettings();
        Grid.SetColumn(saveBtn, 2);
        actionGrid.Children.Add(saveBtn);
        rightStack.Children.Add(actionGrid);

        _statusLabel = new TextBlock
        {
            Text = "提示：关闭窗口不会退出程序，WinDrop 将静默驻留在右下角系统托盘。",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248)),
            FontSize = 11.5,
            Margin = new Thickness(0, 6, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        rightStack.Children.Add(_statusLabel);

        rightCard.Child = rightStack;
        Grid.SetColumn(rightCard, 2);
        rootGrid.Children.Add(rightCard);

        Content = rootGrid;

        Closing += (_, e) =>
        {
            if (!AllowTrueClose)
            {
                e.Cancel = true;
                Hide();
            }
        };

        RefreshQrDisplay();
    }

    private void RefreshQrDisplay()
    {
        string targetUrl = _showingDirectIcloudQr
            ? QrAndPathHelper.OfficialSignedShortcutUrl
            : $"http://{_server.LocalIp}:{_server.ListeningPort}/setup";

        _qrCaption.Text = _showingDirectIcloudQr
            ? "当前：Apple 官方签名快捷指令直装码\n用 iPhone 相机扫码直接添加到「快捷指令」"
            : $"当前：扫码引导页（含一体版与零菜单拆分版）\nhttp://{_server.LocalIp}:{_server.ListeningPort}/setup";

        byte[] png = QrAndPathHelper.GenerateQrPngBytes(targetUrl, 10);
        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(png))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
        }
        _qrImage.Source = bmp;
    }

    private async Task TestPushAsync()
    {
        _server.State.UpdateConfig(c =>
        {
            c.EnablePushNotification = _pushEnableCheck.IsChecked == true;
            c.BarkKeyOrUrl = _barkInputBox.Text.Trim();
            c.ReceiveShortcutName = string.IsNullOrWhiteSpace(_shortcutNameBox.Text)
                ? "AirDrop Plus"
                : _shortcutNameBox.Text.Trim();
            c.PushIncludeClipboardText = _pushIncludeTextCheck.IsChecked == true;
        });

        _statusLabel.Text = "正在向 iPhone 发送测试推送…";
        var (ok, msg) = await _server.PushService.SendShortcutWakePushAsync(
            _server.State.Config,
            "🎉 WinDrop 推送连通成功",
            "轻点此通知即可自动运行快捷指令接收电脑文件");
        _statusLabel.Text = ok ? $"✅ {msg}" : $"⚠️ {msg}";
    }

    private void SaveAllSettings()
    {
        int parsedPort = int.TryParse(_portBox.Text.Trim(), out int p) && p is >= 1024 and <= 65535
            ? p
            : _server.ListeningPort;

        bool portChanged = parsedPort != _server.ListeningPort;
        string exe = Environment.ProcessPath ?? string.Empty;

        _server.State.UpdateConfig(c =>
        {
            c.Port = parsedPort;
            c.Key = _keyBox.Text.Trim();
            c.SaveDirectory = _saveDirBox.Text.Trim();
            c.EnableContextMenu = _ctxMenuCheck.IsChecked == true;
            c.AutoStart = _autoStartCheck.IsChecked == true;
            c.ConvertHeicToJpg = _heicCheck.IsChecked == true;
            c.EnablePushNotification = _pushEnableCheck.IsChecked == true;
            c.BarkKeyOrUrl = _barkInputBox.Text.Trim();
            c.ReceiveShortcutName = string.IsNullOrWhiteSpace(_shortcutNameBox.Text)
                ? "AirDrop Plus"
                : _shortcutNameBox.Text.Trim();
            c.PushIncludeClipboardText = _pushIncludeTextCheck.IsChecked == true;
        });

        if (!string.IsNullOrEmpty(exe))
        {
            if (_ctxMenuCheck.IsChecked == true)
                _sysIntegration.RegisterContextMenu(exe);
            else
                _sysIntegration.UnregisterContextMenu();

            _sysIntegration.SetStartupEnabled(_autoStartCheck.IsChecked == true, exe);
        }

        RefreshQrDisplay();
        _statusLabel.Text = portChanged
            ? "✅ 设置已保存（新端口号将在下次重启 WinDrop 后生效）"
            : "✅ 设置已立即生效！";
    }

    private static FrameworkElement BuildReadOnlyCopyRow(string label, string displayValue, string copyValue)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var val = new TextBlock
        {
            Text = displayValue,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248)),
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(val, 2);
        grid.Children.Add(val);

        var copyBtn = CreateButton("复制域名", System.Windows.Media.Color.FromRgb(51, 65, 85));
        copyBtn.Padding = new Thickness(10, 5, 10, 5);
        copyBtn.Click += (_, _) => System.Windows.Clipboard.SetText(copyValue);
        Grid.SetColumn(copyBtn, 4);
        grid.Children.Add(copyBtn);

        return grid;
    }

    private static StackPanel BuildLabeledInput(string label, System.Windows.Controls.TextBox box)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4)
        });
        sp.Children.Add(box);
        return sp;
    }

    private static System.Windows.Controls.TextBox CreateTextBox(string initial)
    {
        return new System.Windows.Controls.TextBox
        {
            Text = initial,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }

    private static System.Windows.Controls.CheckBox CreateCheckBox(string text, bool isChecked)
    {
        return new System.Windows.Controls.CheckBox
        {
            Content = text,
            IsChecked = isChecked,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            FontSize = 12.5,
            Margin = new Thickness(0, 5, 0, 5)
        };
    }

    private static System.Windows.Controls.Button CreateButton(string text, System.Windows.Media.Color bg)
    {
        return new System.Windows.Controls.Button
        {
            Content = text,
            Foreground = System.Windows.Media.Brushes.White,
            Background = new SolidColorBrush(bg),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 8, 12, 8),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand
        };
    }
}
