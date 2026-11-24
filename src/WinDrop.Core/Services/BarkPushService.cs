using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WinDrop.Core.Models;

namespace WinDrop.Core.Services;

/// <summary>
/// 解析后的 Bark 推送端点信息
/// </summary>
public sealed record BarkEndpointInfo(string ServerBaseUrl, string DeviceKey);

/// <summary>
/// 基于 Bark (APNs) 的 iPhone 原生锁屏/横幅主动推送唤醒服务
/// 支持官方服务器 (https://api.day.app) 与用户私有自建 Bark Server
/// 点击通知横幅直接通过 shortcuts://run-shortcut?name=... 自动唤起接收快捷指令拉取电脑文件
/// </summary>
public sealed class BarkPushService : IPushNotificationService, IDisposable
{
    public const string DefaultBarkServer = "https://api.day.app";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public BarkPushService(HttpClient? customHttpClient = null)
    {
        if (customHttpClient != null)
        {
            _httpClient = customHttpClient;
            _ownsClient = false;
        }
        else
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(6)
            };
            _ownsClient = true;
        }
    }

    /// <summary>
    /// 智能解析用户输入的纯 Key、Bark App 复制出的完整示例链接或自建服务器地址
    /// </summary>
    public static bool TryParseBarkEndpoint(string? rawInput, out BarkEndpointInfo? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(rawInput))
            return false;

        string trimmed = rawInput.Trim();

        // 情况 1：完整的 http:// 或 https:// 链接
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
                return false;

            string[] segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToArray();

            if (segments.Length == 0)
                return false;

            string deviceKey = segments[0];
            if (string.Equals(deviceKey, "push", StringComparison.OrdinalIgnoreCase))
                return false;

            string baseUrl = $"{uri.Scheme}://{uri.Authority}";
            endpoint = new BarkEndpointInfo(baseUrl, deviceKey);
            return true;
        }

        // 情况 2：用户直接粘贴了纯 Key（不含协议前缀）
        string cleanKey = trimmed.Trim('/');
        int slashIdx = cleanKey.IndexOf('/');
        if (slashIdx > 0)
        {
            cleanKey = cleanKey[..slashIdx];
        }

        if (string.IsNullOrWhiteSpace(cleanKey) || cleanKey.Any(char.IsWhiteSpace))
            return false;

        endpoint = new BarkEndpointInfo(DefaultBarkServer, cleanKey);
        return true;
    }

    /// <summary>
    /// 构造点击通知后自动唤起 iOS 快捷指令的 URL Scheme
    /// </summary>
    public static string BuildShortcutRunUrl(string? shortcutName)
    {
        string name = string.IsNullOrWhiteSpace(shortcutName) ? "AirDrop Plus" : shortcutName.Trim();
        return $"shortcuts://run-shortcut?name={Uri.EscapeDataString(name)}";
    }

    public async Task<(bool Success, string Message)> SendShortcutWakePushAsync(
        AppConfig config,
        string title,
        string body,
        string? directUrlOverride = null,
        string? copyTextOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (!config.EnablePushNotification)
        {
            return (false, "主动推送开关当前已关闭。");
        }

        if (!TryParseBarkEndpoint(config.BarkKeyOrUrl, out var endpoint) || endpoint == null)
        {
            return (false, "尚未配置有效的 Bark Key 或服务器地址。");
        }

        string actionUrl = !string.IsNullOrWhiteSpace(directUrlOverride)
            ? directUrlOverride!
            : BuildShortcutRunUrl(config.ReceiveShortcutName);

        var payload = new Dictionary<string, object?>
        {
            ["device_key"] = endpoint.DeviceKey,
            ["title"] = title,
            ["body"] = body,
            ["group"] = "WinDrop",
            ["sound"] = "minuet",
            ["url"] = actionUrl,
            ["isArchive"] = 1
        };

        if (!string.IsNullOrEmpty(copyTextOverride))
        {
            payload["copy"] = copyTextOverride;
            payload["autoCopy"] = "1";
        }

        string pushApiUrl = $"{endpoint.ServerBaseUrl.TrimEnd('/')}/push";

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(pushApiUrl, payload, cancellationToken);
            string respText = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                try
                {
                    using var doc = JsonDocument.Parse(respText);
                    if (doc.RootElement.TryGetProperty("code", out var codeProp) && codeProp.GetInt32() != 200)
                    {
                        string errMsg = doc.RootElement.TryGetProperty("message", out var mp)
                            ? mp.GetString() ?? "未知错误"
                            : "推送服务器返回非 200 状态";
                        return (false, $"推送未送达：{errMsg}");
                    }
                }
                catch
                {
                    // 兼容部分自建代理仅返回 HTTP 200 状态码
                }

                return (true, "已向 iPhone 发送弹窗通知！");
            }

            return (false, $"推送服务响应异常 (HTTP {(int)response.StatusCode})");
        }
        catch (TaskCanceledException)
        {
            return (false, "推送请求超时，请检查电脑是否可访问推送服务器。");
        }
        catch (Exception ex)
        {
            return (false, $"推送发送失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
