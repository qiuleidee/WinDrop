using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WinDrop.Core.Models;

namespace WinDrop.App.Views;

/// <summary>
/// 右下角无焦点抢占可交互通知卡片
/// 收到文件时可直接点击「打开文件」「定位目录」「复制文件」，无需打断当前工作
/// </summary>
public sealed class NotificationToastWindow : Window
{
    private static NotificationToastWindow? _currentToast;

    public static void ShowToast(TransferNotificationEvent ev)
    {
        var app = System.Windows.Application.Current;
        app?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                _currentToast?.Close();
            }
            catch { }

            var win = new NotificationToastWindow(ev);
            _currentToast = win;
            win.Show();
        });
    }

    private NotificationToastWindow(TransferNotificationEvent ev)
    {
        Width = 345;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;

        var accent = ev.IsError
            ? System.Windows.Media.Color.FromRgb(239, 68, 68)
            : System.Windows.Media.Color.FromRgb(16, 185, 129);

        var border = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(15),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 24, 39)),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(1.2),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.5 }
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = ev.Title,
            Foreground = System.Windows.Media.Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 13.5
        });

        stack.Children.Add(new TextBlock
        {
            Text = ev.Message,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(156, 163, 175)),
            FontSize = 12.5,
            Margin = new Thickness(0, 5, 0, string.IsNullOrEmpty(ev.SavedFilePath) ? 0 : 10),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 54
        });

        bool hasFile = !string.IsNullOrEmpty(ev.SavedFilePath) && File.Exists(ev.SavedFilePath);
        if (hasFile)
        {
            string filePath = ev.SavedFilePath!;
            var grid = new UniformGrid { Rows = 1 };

            var openBtn = MakeBtn("打开文件", System.Windows.Media.Color.FromRgb(37, 99, 235));
            openBtn.Margin = new Thickness(0, 0, 6, 0);
            openBtn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo { FileName = filePath, UseShellExecute = true }); } catch { }
                Close();
            };

            var dirBtn = MakeBtn("定位目录", System.Windows.Media.Color.FromRgb(55, 65, 81));
            dirBtn.Margin = new Thickness(0, 0, 6, 0);
            dirBtn.Click += (_, _) =>
            {
                try { Process.Start("explorer.exe", $"/select,\"{filePath}\""); } catch { }
                Close();
            };

            var copyBtn = MakeBtn("复制文件", System.Windows.Media.Color.FromRgb(55, 65, 81));
            copyBtn.Click += (_, _) =>
            {
                try
                {
                    var sc = new StringCollection { filePath };
                    System.Windows.Clipboard.SetFileDropList(sc);
                }
                catch { }
                Close();
            };

            grid.Children.Add(openBtn);
            grid.Children.Add(dirBtn);
            grid.Children.Add(copyBtn);
            stack.Children.Add(grid);
        }

        border.MouseLeftButtonUp += (_, _) =>
        {
            if (!hasFile)
                Close();
        };

        border.Child = stack;
        Content = border;

        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 8;
            Top = area.Bottom - ActualHeight - 8;
        };

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(hasFile ? 7 : 3.8)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };
        timer.Start();
    }

    private static System.Windows.Controls.Button MakeBtn(string text, System.Windows.Media.Color bg)
    {
        return new System.Windows.Controls.Button
        {
            Content = text,
            Foreground = System.Windows.Media.Brushes.White,
            Background = new SolidColorBrush(bg),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand
        };
    }
}
