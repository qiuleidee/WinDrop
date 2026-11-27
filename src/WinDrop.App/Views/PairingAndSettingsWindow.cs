using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WinDrop.App.Services;
using WinDrop.Core.Models;
using WinDrop.Core.Network;
using WinDrop.Core.Server;
using WinDrop.Core.Services;

namespace WinDrop.App.Views;

/// <summary>
/// 原生 WPF「快捷指令安装与连接设置」窗口
/// 采用深色圆角控件体系、沉浸式标题栏与高 DPI 自适应滚动布局，
/// 支持网卡优选锁定、防火墙一键放行、复杂网络联机自检与 Bark 推送测试。
/// </summary>
public sealed class PairingAndSettingsWindow : Window
{
    private readonly WinDropServer _server;
    private readonly WindowsSystemIntegrationService _sysIntegration;

    private readonly System.Windows.Controls.Image _qrImage;
    private readonly TextBlock _qrCaption;
    private readonly TextBlock _serviceCapsulePrimary;
    private readonly TextBlock _serviceCapsuleSecondary;

    private readonly System.Windows.Controls.ComboBox _nicComboBox;
    private readonly List<string> _nicIpValues = new();
    private int _ignoredVirtualCount;

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

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public PairingAndSettingsWindow(WinDropServer server, WindowsSystemIntegrationService sysIntegration)
    {
        _server = server;
        _sysIntegration = sysIntegration;

        Title = "WinDrop — iPhone 快捷指令配对与设置";
        Width = 850;
        Height = 720;
        MinWidth = 800;
        MinHeight = 640;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42));
        Foreground = System.Windows.Media.Brushes.White;

        TryLoadWindowIcon();
        InjectDarkThemeResources();

        SourceInitialized += (_, _) => EnableDarkTitleBar();

        Closing += (_, e) =>
        {
            if (!AllowTrueClose)
            {
                e.Cancel = true;
                Hide();
            }
        };

        var rootGrid = new Grid { Margin = new Thickness(18) };
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(305) });
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ==================== 左侧：扫码配对与服务状态卡片 ====================
        var qrCard = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20)
        };

        var qrLayout = new Grid();
        qrLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        qrLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var qrTopStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };

        qrTopStack.Children.Add(new TextBlock
        {
            Text = "📱 iPhone 扫码安装快捷指令",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6)
        });

        qrTopStack.Children.Add(new TextBlock
        {
            Text = "打开配置引导页，一键获取快捷指令与排障建议",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var qrBox = new Border
        {
            Background = System.Windows.Media.Brushes.White,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        _qrImage = new System.Windows.Controls.Image { Width = 212, Height = 212 };
        qrBox.Child = _qrImage;
        qrTopStack.Children.Add(qrBox);

        _qrCaption = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 213, 225)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            LineHeight = 18,
            Margin = new Thickness(0, 14, 0, 0)
        };
        qrTopStack.Children.Add(_qrCaption);
        Grid.SetRow(qrTopStack, 0);
        qrLayout.Children.Add(qrTopStack);

        // 左侧底部：状态胶囊 + 浏览器直开按钮
        var qrBottomStack = new StackPanel
        {
            Margin = new Thickness(0, 14, 0, 0)
        };

        var capsuleBorder = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var capsuleStack = new StackPanel();
        var primaryLine = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        primaryLine.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 7, 0)
        });
        _serviceCapsulePrimary = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            VerticalAlignment = VerticalAlignment.Center
        };
        primaryLine.Children.Add(_serviceCapsulePrimary);
        capsuleStack.Children.Add(primaryLine);

        _serviceCapsuleSecondary = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        };
        capsuleStack.Children.Add(_serviceCapsuleSecondary);
        capsuleBorder.Child = capsuleStack;
        qrBottomStack.Children.Add(capsuleBorder);

        var openBrowserBtn = CreateButton("🌐 浏览器打开引导页", System.Windows.Media.Color.FromRgb(51, 65, 85));
        openBrowserBtn.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        openBrowserBtn.Padding = new Thickness(10, 8, 10, 8);
        openBrowserBtn.Click += (_, _) => OpenSetupPageInBrowser();
        qrBottomStack.Children.Add(openBrowserBtn);

        Grid.SetRow(qrBottomStack, 1);
        qrLayout.Children.Add(qrBottomStack);

        qrCard.Child = qrLayout;
        Grid.SetColumn(qrCard, 0);
        rootGrid.Children.Add(qrCard);

        // ==================== 右侧：三段式自适应连接与系统设置卡片 ====================
        var settingsCard = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20, 18, 20, 18)
        };

        var rightLayout = new Grid();
        rightLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rightLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 行 0：固定标题区
        var headerTitle = new TextBlock
        {
            Text = "⚙️ 快捷指令连接参数与系统集成",
            FontSize = 15.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252)),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(headerTitle, 0);
        rightLayout.Children.Add(headerTitle);

        // 行 1：带垂直滚动保护的设置内容区
        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 6, 0)
        };
        var cfgStack = new StackPanel();
        var cfg = _server.State.Config;

        // 1. 主机域名行
        cfgStack.Children.Add(BuildInfoRow("主机域名:", _server.PrimaryMdnsDomain, _server.PrimaryMdnsDomain));

        // 2. 网卡与首选 IP 选择区
        cfgStack.Children.Add(new TextBlock
        {
            Text = "局域网网卡 / 首选 IP（自动过滤 TUN 与虚拟网卡）:",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 5)
        });

        var nicGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        nicGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nicGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        nicGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _nicComboBox = CreateComboBox();
        _nicComboBox.SelectionChanged += (_, _) => RefreshQrDisplay();
        Grid.SetColumn(_nicComboBox, 0);
        nicGrid.Children.Add(_nicComboBox);

        var copyIpBtn = CreateButton("复制 IP", System.Windows.Media.Color.FromRgb(51, 65, 85));
        copyIpBtn.Padding = new Thickness(12, 6, 12, 6);
        copyIpBtn.Click += (_, _) =>
        {
            string ip = GetSelectedEffectiveIp();
            System.Windows.Clipboard.SetText(ip);
            ShowStatus($"已复制当前首选 IP：{ip}", false);
        };
        Grid.SetColumn(copyIpBtn, 2);
        nicGrid.Children.Add(copyIpBtn);
        cfgStack.Children.Add(nicGrid);

        // 3. 端口与密钥
        var portKeyGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        portKeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        portKeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        portKeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _portBox = CreateTextBox(cfg.Port.ToString());
        _keyBox = CreateTextBox(cfg.Key);

        var portBlock = BuildLabeledInput("端口 (port):", _portBox);
        Grid.SetColumn(portBlock, 0);
        portKeyGrid.Children.Add(portBlock);

        var keyBlock = BuildLabeledInput("密钥 (key):", _keyBox);
        Grid.SetColumn(keyBlock, 2);
        portKeyGrid.Children.Add(keyBlock);
        cfgStack.Children.Add(portKeyGrid);

        // 4. 接收文件保存目录
        cfgStack.Children.Add(new TextBlock
        {
            Text = "接收文件保存目录:",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 5)
        });

        var dirGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _saveDirBox = CreateTextBox(cfg.SaveDirectory);
        Grid.SetColumn(_saveDirBox, 0);
        dirGrid.Children.Add(_saveDirBox);

        var browseBtn = CreateButton("浏览...", System.Windows.Media.Color.FromRgb(51, 65, 85));
        browseBtn.Padding = new Thickness(12, 6, 12, 6);
        browseBtn.Click += (_, _) => BrowseSaveDirectory();
        Grid.SetColumn(browseBtn, 2);
        dirGrid.Children.Add(browseBtn);

        var openDirBtn = CreateButton("打开", System.Windows.Media.Color.FromRgb(51, 65, 85));
        openDirBtn.Padding = new Thickness(12, 6, 12, 6);
        openDirBtn.Click += (_, _) => OpenSaveDirectory();
        Grid.SetColumn(openDirBtn, 4);
        dirGrid.Children.Add(openDirBtn);
        cfgStack.Children.Add(dirGrid);

        // 5. 系统集成复选框组
        _ctxMenuCheck = CreateCheckBox("启用资源管理器右键「通过 WinDrop 分享」菜单", _sysIntegration.IsContextMenuRegistered());
        _autoStartCheck = CreateCheckBox("开机自动启动并最小化驻留系统托盘", cfg.AutoStart);
        _heicCheck = CreateCheckBox("收到 iPhone 的 HEIC 照片时自动转换为 JPG", cfg.ConvertHeicToJpg);

        cfgStack.Children.Add(_ctxMenuCheck);
        cfgStack.Children.Add(_autoStartCheck);
        cfgStack.Children.Add(_heicCheck);

        // 6. Bark 主动推送卡片
        var barkSection = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 58, 95)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 10, 0, 2)
        };
        var barkStack = new StackPanel();
        barkStack.Children.Add(new TextBlock
        {
            Text = "🔔 iPhone 主动推送唤醒（可选 · 配合免费 Bark App）",
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        barkStack.Children.Add(new TextBlock
        {
            Text = "Bark Key 或地址（填纯 Key 或复制的链接，留空则不推送）:",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 0, 5)
        });

        var barkKeyGrid = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        barkKeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        barkKeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        barkKeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _barkInputBox = CreateTextBox(cfg.BarkKeyOrUrl);
        Grid.SetColumn(_barkInputBox, 0);
        barkKeyGrid.Children.Add(_barkInputBox);

        var testPushBtn = CreateButton("🔔 测试推送", System.Windows.Media.Color.FromRgb(14, 116, 144));
        testPushBtn.Padding = new Thickness(12, 6, 12, 6);
        testPushBtn.Click += async (_, _) => await TestPushAsync();
        Grid.SetColumn(testPushBtn, 2);
        barkKeyGrid.Children.Add(testPushBtn);
        barkStack.Children.Add(barkKeyGrid);

        // 精简左侧标签文本，把补充说明放进 ToolTip，保证输入框宽度充裕
        var shortcutRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        shortcutRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shortcutRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        shortcutRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var shortcutLbl = new TextBlock
        {
            Text = "唤醒快捷指令名:",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "填写 iPhone 快捷指令完整名称；拆分版请填「WinDrop接收」或「从电脑接收」"
        };
        Grid.SetColumn(shortcutLbl, 0);
        shortcutRow.Children.Add(shortcutLbl);

        _shortcutNameBox = CreateTextBox(cfg.ReceiveShortcutName);
        _shortcutNameBox.ToolTip = "填写 iPhone 快捷指令完整名称；拆分版请填「WinDrop接收」或「从电脑接收」";
        Grid.SetColumn(_shortcutNameBox, 2);
        shortcutRow.Children.Add(_shortcutNameBox);
        barkStack.Children.Add(shortcutRow);

        _pushEnableCheck = CreateCheckBox("启用右键分享与热键 (Ctrl+Shift+D) 主动推送通知", cfg.EnablePushNotification);
        _pushIncludeTextCheck = CreateCheckBox("推送文本或网址时附带内容（支持下拉一键复制或直开网页）", cfg.PushIncludeClipboardText);
        barkStack.Children.Add(_pushEnableCheck);
        barkStack.Children.Add(_pushIncludeTextCheck);

        barkSection.Child = barkStack;
        cfgStack.Children.Add(barkSection);

        scrollViewer.Content = cfgStack;
        Grid.SetRow(scrollViewer, 1);
        rightLayout.Children.Add(scrollViewer);

        // 行 2：固定底部操作按钮与多行状态栏
        var bottomFooter = new StackPanel
        {
            Margin = new Thickness(0, 12, 0, 0)
        };

        var btnGrid = new Grid();
        btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var fwBtn = CreateButton("🛡️ 放行防火墙", System.Windows.Media.Color.FromRgb(71, 85, 105));
        fwBtn.Click += async (_, _) => await EnsureFirewallAsync();
        Grid.SetColumn(fwBtn, 0);
        btnGrid.Children.Add(fwBtn);

        var diagBtn = CreateButton("🔍 联机自检", System.Windows.Media.Color.FromRgb(15, 118, 110));
        diagBtn.Click += (_, _) => RunNetworkDiagnostics();
        Grid.SetColumn(diagBtn, 2);
        btnGrid.Children.Add(diagBtn);

        var saveBtn = CreateButton("💾 保存设置", System.Windows.Media.Color.FromRgb(37, 99, 235));
        saveBtn.Click += (_, _) => SaveAllSettings();
        Grid.SetColumn(saveBtn, 4);
        btnGrid.Children.Add(saveBtn);

        bottomFooter.Children.Add(btnGrid);

        _statusLabel = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 12,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 34,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184))
        };
        bottomFooter.Children.Add(_statusLabel);

        Grid.SetRow(bottomFooter, 2);
        rightLayout.Children.Add(bottomFooter);

        settingsCard.Child = rightLayout;
        Grid.SetColumn(settingsCard, 2);
        rootGrid.Children.Add(settingsCard);

        Content = rootGrid;

        PopulateNicComboBox();
        RefreshQrDisplay();
        InitializeDefaultStatusTip();
    }

    /// <summary>
    /// 向窗口注入深色圆角控件资源字典，替代 Windows 默认浅色 Aero2 控件外观
    /// </summary>
    private void InjectDarkThemeResources()
    {
        const string themeXaml = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Style x:Key="DarkButtonStyle" TargetType="Button">
                    <Setter Property="Foreground" Value="#FFFFFF" />
                    <Setter Property="BorderThickness" Value="0" />
                    <Setter Property="Padding" Value="10,8" />
                    <Setter Property="FontSize" Value="12.5" />
                    <Setter Property="FontWeight" Value="SemiBold" />
                    <Setter Property="Cursor" Value="Hand" />
                    <Setter Property="SnapsToDevicePixels" Value="True" />
                    <Setter Property="Template">
                        <Setter.Value>
                            <ControlTemplate TargetType="Button">
                                <Border x:Name="BtnBorder"
                                        Background="{TemplateBinding Background}"
                                        BorderBrush="{TemplateBinding BorderBrush}"
                                        BorderThickness="{TemplateBinding BorderThickness}"
                                        CornerRadius="6"
                                        SnapsToDevicePixels="True">
                                    <Grid>
                                        <Border x:Name="HoverOverlay"
                                                Background="#FFFFFF"
                                                Opacity="0"
                                                CornerRadius="6"
                                                IsHitTestVisible="False" />
                                        <ContentPresenter HorizontalAlignment="Center"
                                                          VerticalAlignment="Center"
                                                          Margin="{TemplateBinding Padding}" />
                                    </Grid>
                                </Border>
                                <ControlTemplate.Triggers>
                                    <Trigger Property="IsMouseOver" Value="True">
                                        <Setter TargetName="HoverOverlay" Property="Opacity" Value="0.12" />
                                    </Trigger>
                                    <Trigger Property="IsPressed" Value="True">
                                        <Setter TargetName="HoverOverlay" Property="Opacity" Value="0.22" />
                                    </Trigger>
                                    <Trigger Property="IsEnabled" Value="False">
                                        <Setter Property="Opacity" Value="0.45" />
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>

                <Style x:Key="DarkTextBoxStyle" TargetType="TextBox">
                    <Setter Property="Background" Value="#0B1220" />
                    <Setter Property="Foreground" Value="#F8FAFC" />
                    <Setter Property="CaretBrush" Value="#38BDF8" />
                    <Setter Property="SelectionBrush" Value="#2563EB" />
                    <Setter Property="BorderBrush" Value="#334155" />
                    <Setter Property="BorderThickness" Value="1" />
                    <Setter Property="Padding" Value="10,7" />
                    <Setter Property="FontSize" Value="13" />
                    <Setter Property="VerticalContentAlignment" Value="Center" />
                    <Setter Property="SnapsToDevicePixels" Value="True" />
                    <Setter Property="Template">
                        <Setter.Value>
                            <ControlTemplate TargetType="TextBox">
                                <Border x:Name="TbBorder"
                                        Background="{TemplateBinding Background}"
                                        BorderBrush="{TemplateBinding BorderBrush}"
                                        BorderThickness="{TemplateBinding BorderThickness}"
                                        CornerRadius="6"
                                        SnapsToDevicePixels="True">
                                    <ScrollViewer x:Name="PART_ContentHost"
                                                  Margin="0"
                                                  Padding="{TemplateBinding Padding}"
                                                  Focusable="False"
                                                  HorizontalScrollBarVisibility="Hidden"
                                                  VerticalScrollBarVisibility="Hidden"
                                                  VerticalAlignment="Center" />
                                </Border>
                                <ControlTemplate.Triggers>
                                    <Trigger Property="IsMouseOver" Value="True">
                                        <Setter TargetName="TbBorder" Property="BorderBrush" Value="#475569" />
                                    </Trigger>
                                    <Trigger Property="IsKeyboardFocused" Value="True">
                                        <Setter TargetName="TbBorder" Property="BorderBrush" Value="#38BDF8" />
                                    </Trigger>
                                    <Trigger Property="IsEnabled" Value="False">
                                        <Setter Property="Opacity" Value="0.5" />
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>

                <Style x:Key="DarkCheckBoxStyle" TargetType="CheckBox">
                    <Setter Property="Foreground" Value="#E2E8F0" />
                    <Setter Property="FontSize" Value="12.5" />
                    <Setter Property="Cursor" Value="Hand" />
                    <Setter Property="Margin" Value="0,4,0,4" />
                    <Setter Property="SnapsToDevicePixels" Value="True" />
                    <Setter Property="Template">
                        <Setter.Value>
                            <ControlTemplate TargetType="CheckBox">
                                <Grid Background="Transparent">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="Auto" />
                                        <ColumnDefinition Width="*" />
                                    </Grid.ColumnDefinitions>
                                    <Border x:Name="CheckBoxBorder"
                                            Grid.Column="0"
                                            Width="17"
                                            Height="17"
                                            CornerRadius="4"
                                            Background="#0B1220"
                                            BorderBrush="#475569"
                                            BorderThickness="1.5"
                                            Margin="0,1,9,0"
                                            VerticalAlignment="Center">
                                        <Path x:Name="CheckMark"
                                              Data="M 3,8.5 L 6.5,12 L 12.5,4.5"
                                              Stroke="#FFFFFF"
                                              StrokeThickness="2"
                                              StrokeStartLineCap="Round"
                                              StrokeEndLineCap="Round"
                                              StrokeLineJoin="Round"
                                              Visibility="Collapsed" />
                                    </Border>
                                    <ContentPresenter Grid.Column="1"
                                                      VerticalAlignment="Center"
                                                      RecognizesAccessKey="True">
                                        <ContentPresenter.Resources>
                                            <Style TargetType="TextBlock">
                                                <Setter Property="TextWrapping" Value="Wrap" />
                                            </Style>
                                        </ContentPresenter.Resources>
                                    </ContentPresenter>
                                </Grid>
                                <ControlTemplate.Triggers>
                                    <Trigger Property="IsMouseOver" Value="True">
                                        <Setter TargetName="CheckBoxBorder" Property="BorderBrush" Value="#38BDF8" />
                                    </Trigger>
                                    <Trigger Property="IsChecked" Value="True">
                                        <Setter TargetName="CheckBoxBorder" Property="Background" Value="#2563EB" />
                                        <Setter TargetName="CheckBoxBorder" Property="BorderBrush" Value="#3B82F6" />
                                        <Setter TargetName="CheckMark" Property="Visibility" Value="Visible" />
                                    </Trigger>
                                    <Trigger Property="IsEnabled" Value="False">
                                        <Setter Property="Opacity" Value="0.5" />
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>

                <Style x:Key="DarkComboBoxItemStyle" TargetType="ComboBoxItem">
                    <Setter Property="Foreground" Value="#E2E8F0" />
                    <Setter Property="Background" Value="Transparent" />
                    <Setter Property="Padding" Value="10,7" />
                    <Setter Property="FontSize" Value="12.5" />
                    <Setter Property="Cursor" Value="Hand" />
                    <Setter Property="Template">
                        <Setter.Value>
                            <ControlTemplate TargetType="ComboBoxItem">
                                <Border x:Name="ItemBorder"
                                        Background="{TemplateBinding Background}"
                                        CornerRadius="4"
                                        Padding="{TemplateBinding Padding}"
                                        SnapsToDevicePixels="True">
                                    <ContentPresenter VerticalAlignment="Center" />
                                </Border>
                                <ControlTemplate.Triggers>
                                    <Trigger Property="IsHighlighted" Value="True">
                                        <Setter TargetName="ItemBorder" Property="Background" Value="#334155" />
                                        <Setter Property="Foreground" Value="#FFFFFF" />
                                    </Trigger>
                                    <Trigger Property="IsSelected" Value="True">
                                        <Setter TargetName="ItemBorder" Property="Background" Value="#1D4ED8" />
                                        <Setter Property="Foreground" Value="#FFFFFF" />
                                    </Trigger>
                                </ControlTemplate.Triggers>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>

                <Style x:Key="DarkComboBoxStyle" TargetType="ComboBox">
                    <Setter Property="Background" Value="#0B1220" />
                    <Setter Property="Foreground" Value="#F8FAFC" />
                    <Setter Property="BorderBrush" Value="#334155" />
                    <Setter Property="BorderThickness" Value="1" />
                    <Setter Property="FontSize" Value="12.5" />
                    <Setter Property="Height" Value="34" />
                    <Setter Property="Cursor" Value="Hand" />
                    <Setter Property="ItemContainerStyle" Value="{StaticResource DarkComboBoxItemStyle}" />
                    <Setter Property="Template">
                        <Setter.Value>
                            <ControlTemplate TargetType="ComboBox">
                                <Grid SnapsToDevicePixels="True">
                                    <ToggleButton x:Name="ToggleButton"
                                                  Focusable="False"
                                                  IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}"
                                                  ClickMode="Press">
                                        <ToggleButton.Template>
                                            <ControlTemplate TargetType="ToggleButton">
                                                <Border x:Name="CbBorder"
                                                        Background="#0B1220"
                                                        BorderBrush="#334155"
                                                        BorderThickness="1"
                                                        CornerRadius="6"
                                                        SnapsToDevicePixels="True">
                                                    <Grid>
                                                        <Grid.ColumnDefinitions>
                                                            <ColumnDefinition Width="*" />
                                                            <ColumnDefinition Width="28" />
                                                        </Grid.ColumnDefinitions>
                                                        <Path Grid.Column="1"
                                                              Data="M 0,0 L 4.5,4.5 L 9,0"
                                                              Stroke="#94A3B8"
                                                              StrokeThickness="1.6"
                                                              StrokeStartLineCap="Round"
                                                              StrokeEndLineCap="Round"
                                                              HorizontalAlignment="Center"
                                                              VerticalAlignment="Center" />
                                                    </Grid>
                                                </Border>
                                                <ControlTemplate.Triggers>
                                                    <Trigger Property="IsMouseOver" Value="True">
                                                        <Setter TargetName="CbBorder" Property="BorderBrush" Value="#38BDF8" />
                                                    </Trigger>
                                                    <Trigger Property="IsChecked" Value="True">
                                                        <Setter TargetName="CbBorder" Property="BorderBrush" Value="#38BDF8" />
                                                    </Trigger>
                                                </ControlTemplate.Triggers>
                                            </ControlTemplate>
                                        </ToggleButton.Template>
                                    </ToggleButton>
                                    <ContentPresenter x:Name="ContentSite"
                                                      IsHitTestVisible="False"
                                                      Content="{TemplateBinding SelectionBoxItem}"
                                                      ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                                                      ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"
                                                      Margin="10,0,28,0"
                                                      VerticalAlignment="Center"
                                                      HorizontalAlignment="Left">
                                        <ContentPresenter.Resources>
                                            <Style TargetType="TextBlock">
                                                <Setter Property="Foreground" Value="#F8FAFC" />
                                                <Setter Property="TextTrimming" Value="CharacterEllipsis" />
                                            </Style>
                                        </ContentPresenter.Resources>
                                    </ContentPresenter>
                                    <Popup x:Name="Popup"
                                           Placement="Bottom"
                                           IsOpen="{TemplateBinding IsDropDownOpen}"
                                           AllowsTransparency="True"
                                           Focusable="False"
                                           PopupAnimation="Slide">
                                        <Border MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"
                                                MaxHeight="{TemplateBinding MaxDropDownHeight}"
                                                Background="#1E293B"
                                                BorderBrush="#475569"
                                                BorderThickness="1"
                                                CornerRadius="6"
                                                Margin="0,4,0,0"
                                                SnapsToDevicePixels="True">
                                            <ScrollViewer Margin="4"
                                                          VerticalScrollBarVisibility="Auto"
                                                          SnapsToDevicePixels="True">
                                                <StackPanel IsItemsHost="True"
                                                            KeyboardNavigation.DirectionalNavigation="Contained" />
                                            </ScrollViewer>
                                        </Border>
                                    </Popup>
                                </Grid>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
            </ResourceDictionary>
            """;

        var dict = (ResourceDictionary)XamlReader.Parse(themeXaml);
        Resources.MergedDictionaries.Add(dict);
    }

    private void TryLoadWindowIcon()
    {
        try
        {
            string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath))
            {
                Icon = BitmapFrame.Create(new Uri(iconPath));
            }
        }
        catch
        {
            // 图标加载失败不影响窗口展示
        }
    }

    private void EnableDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int useImmersiveDarkMode = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref useImmersiveDarkMode, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, 19, ref useImmersiveDarkMode, sizeof(int));
            }
        }
        catch
        {
            // 不支持 DWM 深色标题栏的系统版本静默忽略
        }
    }

    private void PopulateNicComboBox()
    {
        _nicComboBox.Items.Clear();
        _nicIpValues.Clear();

        var endpoints = NetworkHelper.GetLocalIPv4Endpoints();
        string autoPrimary = NetworkHelper.GetPrimaryLocalIPv4(null).ToString();
        _ignoredVirtualCount = endpoints.Count(e => e.IsVirtualOrTunnel);

        _nicIpValues.Add("");
        _nicComboBox.Items.Add($"自动优选（当前：{autoPrimary}）");

        string savedPreferred = _server.State.Config.PreferredLocalIp?.Trim() ?? "";
        int selectedIndex = 0;

        foreach (var ep in endpoints)
        {
            string ipStr = ep.IpAddress.ToString();
            _nicIpValues.Add(ipStr);
            string suffix = ep.IsVirtualOrTunnel ? " [虚拟/TUN]" : "";
            string label = $"{ipStr} · {ep.CategoryLabel} ({ep.InterfaceName}){suffix}";
            int idx = _nicComboBox.Items.Add(label);

            if (!string.IsNullOrEmpty(savedPreferred) &&
                string.Equals(ipStr, savedPreferred, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = idx;
            }
        }

        _nicComboBox.SelectedIndex = selectedIndex;
    }

    private string GetSelectedEffectiveIp()
    {
        int idx = _nicComboBox.SelectedIndex;
        if (idx > 0 && idx < _nicIpValues.Count && !string.IsNullOrWhiteSpace(_nicIpValues[idx]))
        {
            return _nicIpValues[idx];
        }

        return NetworkHelper.GetPrimaryLocalIPv4(null).ToString();
    }

    private string BuildCurrentSetupUrl()
    {
        return $"http://{GetSelectedEffectiveIp()}:{_server.ListeningPort}/setup";
    }

    private void RefreshQrDisplay()
    {
        string url = BuildCurrentSetupUrl();
        byte[] qrBytes = QrAndPathHelper.GenerateQrPngBytes(url, 8);

        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(qrBytes))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
        }

        _qrImage.Source = bmp;
        _qrCaption.Text = $"用 iPhone 相机扫码打开配置引导页\n{url}";

        _serviceCapsulePrimary.Text = $"服务运行中 · 端口 {_server.ListeningPort}";
        _serviceCapsuleSecondary.Text = _ignoredVirtualCount > 0
            ? $"已自动过滤 {_ignoredVirtualCount} 个虚拟 / TUN 网卡"
            : "当前未检测到虚拟网卡干扰";
    }

    private void OpenSetupPageInBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(BuildCurrentSetupUrl()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus($"无法打开浏览器：{ex.Message}", true);
        }
    }

    private void InitializeDefaultStatusTip()
    {
        bool fwOk = _sysIntegration.IsFirewallRuleInstalled(_server.ListeningPort);
        if (!fwOk)
        {
            ShowStatus("⚠️ 当前端口尚未放行防火墙，建议先点击「🛡️ 放行防火墙」，再点击「🔍 联机自检」验证连通性。", true);
        }
        else
        {
            ShowStatus("💡 若开启了代理软件 TUN / 虚拟网卡或无法连接，可点击「🔍 联机自检」排查或在上方锁定物理网卡 IP。", false);
        }
    }

    private async Task EnsureFirewallAsync()
    {
        try
        {
            ShowStatus("正在向系统申请 UAC 权限以放行入站规则...", false);
            var (ok, msg) = await _sysIntegration.EnsureFirewallRuleAsync(_server.ListeningPort);
            ShowStatus(ok ? $"✅ {msg}" : $"⚠️ {msg}", !ok);
        }
        catch (Exception ex)
        {
            ShowStatus($"防火墙配置失败：{ex.Message}", true);
        }
    }

    private void RunNetworkDiagnostics()
    {
        try
        {
            string? preferred = _nicComboBox.SelectedIndex > 0 && _nicComboBox.SelectedIndex < _nicIpValues.Count
                ? _nicIpValues[_nicComboBox.SelectedIndex]
                : null;
            bool fwOk = _sysIntegration.IsFirewallRuleInstalled(_server.ListeningPort);
            var report = NetworkHelper.DiagnoseEnvironment(_server.ListeningPort, fwOk, preferred);

            var sb = new StringBuilder();
            sb.AppendLine($"当前推荐通信 IP：{report.PrimaryIp}（端口 {_server.ListeningPort}）");
            sb.AppendLine($"防火墙入站规则：{(report.FirewallConfigured ? "✅ 已放行" : "❌ 未配置或存在公用网段拦截")}");
            sb.AppendLine(report.HasSystemProxy
                ? $"Windows 系统代理：⚠️ 已开启 ({report.SystemProxyAddress})"
                : "Windows 系统代理：未启用");

            sb.AppendLine();
            sb.AppendLine($"可用物理/无线局域网 IP（共 {report.CandidateEndpoints.Count} 个）：");
            foreach (var ep in report.CandidateEndpoints)
            {
                sb.AppendLine($"  • {ep.IpAddress} [{ep.CategoryLabel}] - {ep.InterfaceName}");
            }

            if (report.IgnoredVirtualEndpoints.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"已自动屏蔽的虚拟 / TUN 网卡（共 {report.IgnoredVirtualEndpoints.Count} 个，防止手机拿到假地址）：");
                foreach (var vp in report.IgnoredVirtualEndpoints)
                {
                    sb.AppendLine($"  • {vp.IpAddress} [{vp.CategoryLabel}] - {vp.InterfaceName}");
                }
            }

            if (report.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("检测到风险项：");
                foreach (var w in report.Warnings)
                {
                    sb.AppendLine($"  ⚠ {w}");
                }
            }

            if (report.Suggestions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("排障建议：");
                foreach (var s in report.Suggestions)
                {
                    sb.AppendLine($"  → {s}");
                }
            }

            System.Windows.MessageBox.Show(
                this,
                sb.ToString().TrimEnd(),
                "WinDrop 复杂网络联机自检报告",
                MessageBoxButton.OK,
                report.Warnings.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            ShowStatus(
                report.Warnings.Count > 0
                    ? $"自检发现 {report.Warnings.Count} 项注意事项，请查看弹窗建议。"
                    : $"✅ 网络环境健康，推荐使用 {report.PrimaryIp} 与 iPhone 通信。",
                report.Warnings.Count > 0);
        }
        catch (Exception ex)
        {
            ShowStatus($"网络自检出错：{ex.Message}", true);
        }
    }

    private async Task TestPushAsync()
    {
        var tempConfig = new AppConfig
        {
            EnablePushNotification = true,
            BarkKeyOrUrl = _barkInputBox.Text.Trim(),
            ReceiveShortcutName = string.IsNullOrWhiteSpace(_shortcutNameBox.Text)
                ? "WinDrop接收"
                : _shortcutNameBox.Text.Trim()
        };

        ShowStatus("正在向 Bark 发送测试唤醒通知...", false);
        var (ok, msg) = await _server.PushService.SendShortcutWakePushAsync(
            tempConfig,
            "WinDrop 推送连通性测试",
            "收到此通知即代表桌面端可随时唤醒 iPhone 接收文件！");
        ShowStatus(ok ? $"✅ {msg}" : $"❌ {msg}", !ok);
    }

    private void SaveAllSettings()
    {
        if (!int.TryParse(_portBox.Text.Trim(), out int port) || port < 1024 || port > 65535)
        {
            ShowStatus("端口号必须在 1024 ~ 65535 之间。", true);
            return;
        }

        string key = _keyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            ShowStatus("密钥不能为空。", true);
            return;
        }

        string saveDir = _saveDirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(saveDir))
        {
            ShowStatus("接收目录不能为空。", true);
            return;
        }

        try
        {
            Directory.CreateDirectory(saveDir);
        }
        catch (Exception ex)
        {
            ShowStatus($"保存目录无效：{ex.Message}", true);
            return;
        }

        string preferredIp = _nicComboBox.SelectedIndex > 0 && _nicComboBox.SelectedIndex < _nicIpValues.Count
            ? _nicIpValues[_nicComboBox.SelectedIndex]
            : string.Empty;
        bool ctxEnabled = _ctxMenuCheck.IsChecked == true;
        bool autoStart = _autoStartCheck.IsChecked == true;
        bool heic = _heicCheck.IsChecked == true;
        bool pushEnabled = _pushEnableCheck.IsChecked == true;
        bool pushIncludeText = _pushIncludeTextCheck.IsChecked == true;
        string barkKey = _barkInputBox.Text.Trim();
        string shortcutName = string.IsNullOrWhiteSpace(_shortcutNameBox.Text)
            ? "WinDrop接收"
            : _shortcutNameBox.Text.Trim();

        _server.State.UpdateConfig(cfg =>
        {
            cfg.Port = port;
            cfg.Key = key;
            cfg.PreferredLocalIp = preferredIp;
            cfg.SaveDirectory = saveDir;
            cfg.ConvertHeicToJpg = heic;
            cfg.AutoStart = autoStart;
            cfg.EnableContextMenu = ctxEnabled;
            cfg.EnablePushNotification = pushEnabled;
            cfg.BarkKeyOrUrl = barkKey;
            cfg.ReceiveShortcutName = shortcutName;
            cfg.PushIncludeClipboardText = pushIncludeText;
        });

        string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            if (ctxEnabled)
                _sysIntegration.RegisterContextMenu(exePath);
            else
                _sysIntegration.UnregisterContextMenu();

            _sysIntegration.SetStartupEnabled(autoStart, exePath);
        }

        _server.RefreshNetworkBindings();
        RefreshQrDisplay();

        bool portChanged = port != _server.ListeningPort;
        ShowStatus(
            portChanged
                ? "✅ 设置已保存（网卡选择、密钥与推送配置已立即生效；新端口需重启 WinDrop 生效）。"
                : "✅ 所有设置已保存并立即生效！",
            false);
    }

    private void BrowseSaveDirectory()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择接收来自 iPhone 文件的默认保存目录",
            SelectedPath = _saveDirBox.Text
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _saveDirBox.Text = dlg.SelectedPath;
        }
    }

    private void OpenSaveDirectory()
    {
        string dir = _saveDirBox.Text.Trim();
        if (Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        else
        {
            ShowStatus("保存目录尚不存在，请先点击「💾 保存设置」自动创建。", true);
        }
    }

    private void ShowStatus(string message, bool isError)
    {
        _statusLabel.Text = message;
        _statusLabel.Foreground = new SolidColorBrush(isError
            ? System.Windows.Media.Color.FromRgb(248, 113, 113)
            : System.Windows.Media.Color.FromRgb(74, 222, 128));
    }

    private Grid BuildInfoRow(string label, string displayValue, string copyValue)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
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
        copyBtn.Padding = new Thickness(12, 6, 12, 6);
        copyBtn.Click += (_, _) =>
        {
            System.Windows.Clipboard.SetText(copyValue);
            ShowStatus($"已复制主机域名：{copyValue}", false);
        };
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
            Margin = new Thickness(0, 0, 0, 5)
        });
        sp.Children.Add(box);
        return sp;
    }

    private System.Windows.Controls.TextBox CreateTextBox(string initial)
    {
        return new System.Windows.Controls.TextBox
        {
            Text = initial,
            Style = (Style)FindResource("DarkTextBoxStyle")
        };
    }

    private System.Windows.Controls.ComboBox CreateComboBox()
    {
        return new System.Windows.Controls.ComboBox
        {
            Style = (Style)FindResource("DarkComboBoxStyle")
        };
    }

    private System.Windows.Controls.CheckBox CreateCheckBox(string text, bool isChecked)
    {
        return new System.Windows.Controls.CheckBox
        {
            Content = text,
            IsChecked = isChecked,
            Style = (Style)FindResource("DarkCheckBoxStyle")
        };
    }

    private System.Windows.Controls.Button CreateButton(string text, System.Windows.Media.Color bg)
    {
        return new System.Windows.Controls.Button
        {
            Content = text,
            Background = new SolidColorBrush(bg),
            Style = (Style)FindResource("DarkButtonStyle")
        };
    }
}
