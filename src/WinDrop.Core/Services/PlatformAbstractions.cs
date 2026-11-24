using QRCoder;
using WinDrop.Core.Models;

namespace WinDrop.Core.Services;

/// <summary>
/// 剪贴板平台抽象接口：支持读取/写入文本、文件列表（CF_HDROP）与截图（PNG Base64）
/// </summary>
public interface IClipboardProvider
{
    Task<ClipboardSnapshot> GetClipboardSnapshotAsync();
    Task<bool> SetTextAsync(string text);
    Task<bool> SetFilesAsync(IReadOnlyList<string> filePaths);
    Task<bool> SetImagePngAsync(byte[] pngBytes);
}

/// <summary>
/// HEIC 图像转换接口
/// </summary>
public interface IHeicConverter
{
    Task<string?> TryConvertHeicToJpgAsync(string heicFilePath, bool keepOriginal);
}

/// <summary>
/// 系统集成服务接口（右键菜单、防火墙放行、开机自启）
/// </summary>
public interface ISystemIntegrationService
{
    bool IsFirewallRuleInstalled(int port);
    Task<(bool Success, string Message)> EnsureFirewallRuleAsync(int port);
    bool IsContextMenuRegistered();
    (bool Success, string Message) RegisterContextMenu(string executablePath);
    (bool Success, string Message) UnregisterContextMenu();
    void SetStartupEnabled(bool enabled, string executablePath);
}

/// <summary>
/// 内存剪贴板实现（用于自动化集成测试）
/// </summary>
public sealed class InMemoryClipboardProvider : IClipboardProvider
{
    private ClipboardSnapshot _snapshot = ClipboardSnapshot.Empty;
    private readonly object _lock = new();

    public Task<ClipboardSnapshot> GetClipboardSnapshotAsync()
    {
        lock (_lock)
        {
            return Task.FromResult(_snapshot);
        }
    }

    public Task<bool> SetTextAsync(string text)
    {
        lock (_lock)
        {
            _snapshot = new ClipboardSnapshot { Type = ClipboardDataType.Text, Text = text };
            return Task.FromResult(true);
        }
    }

    public Task<bool> SetFilesAsync(IReadOnlyList<string> filePaths)
    {
        lock (_lock)
        {
            var valid = filePaths.Where(File.Exists).ToList();
            if (valid.Count == 0)
                return Task.FromResult(false);
            _snapshot = new ClipboardSnapshot { Type = ClipboardDataType.File, FilePaths = valid };
            return Task.FromResult(true);
        }
    }

    public Task<bool> SetImagePngAsync(byte[] pngBytes)
    {
        lock (_lock)
        {
            _snapshot = new ClipboardSnapshot
            {
                Type = ClipboardDataType.Image,
                ImagePngBase64 = Convert.ToBase64String(pngBytes)
            };
            return Task.FromResult(true);
        }
    }
}

public sealed class NullHeicConverter : IHeicConverter
{
    public Task<string?> TryConvertHeicToJpgAsync(string heicFilePath, bool keepOriginal) => Task.FromResult<string?>(null);
}

public sealed class NullSystemIntegrationService : ISystemIntegrationService
{
    public bool IsFirewallRuleInstalled(int port) => true;
    public Task<(bool Success, string Message)> EnsureFirewallRuleAsync(int port) => Task.FromResult((true, "已跳过"));
    public bool IsContextMenuRegistered() => false;
    public (bool Success, string Message) RegisterContextMenu(string executablePath) => (true, "已跳过");
    public (bool Success, string Message) UnregisterContextMenu() => (true, "已跳过");
    public void SetStartupEnabled(bool enabled, string executablePath) { }
}

/// <summary>
/// 主动推送通知服务接口（用于电脑右键分享或热键触发时唤醒 iPhone 弹窗一键接收）
/// </summary>
public interface IPushNotificationService
{
    Task<(bool Success, string Message)> SendShortcutWakePushAsync(
        AppConfig config,
        string title,
        string body,
        string? directUrlOverride = null,
        string? copyTextOverride = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 默认空推送实现（未启用或无头测试默认桩）
/// </summary>
public sealed class NullPushNotificationService : IPushNotificationService
{
    public Task<(bool Success, string Message)> SendShortcutWakePushAsync(
        AppConfig config,
        string title,
        string body,
        string? directUrlOverride = null,
        string? copyTextOverride = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult((false, "未配置推送服务"));
}

/// <summary>
/// 二维码与路径 Base64 编解码工具
/// </summary>
public static class QrAndPathHelper
{
    public static byte[] GenerateQrPngBytes(string content, int pixelsPerModule = 12)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        using var pngQr = new PngByteQRCode(data);
        return pngQr.GetGraphic(pixelsPerModule);
    }

    public static string EncodeFilePathBase64(string fullPath)
    {
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(fullPath));
    }

    public static string? DecodeFilePathBase64(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
            return null;

        try
        {
            string normalized = Uri.UnescapeDataString(encoded.Trim())
                .Replace('-', '+')
                .Replace('_', '/');
            switch (normalized.Length % 4)
            {
                case 2: normalized += "=="; break;
                case 3: normalized += "="; break;
            }
            byte[] bytes = Convert.FromBase64String(normalized);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}
