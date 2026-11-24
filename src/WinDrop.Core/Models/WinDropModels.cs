namespace WinDrop.Core.Models;

/// <summary>
/// WinDrop 极简核心配置（持久化于程序目录或 AppData 下的 config.json）
/// </summary>
public sealed class AppConfig
{
    /// <summary>HTTP 服务监听端口（默认 58446，与官方快捷指令默认习惯一致）</summary>
    public int Port { get; set; } = 58446;

    /// <summary>访问密钥（快捷指令 Authorization 请求头校验）</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>接收文件的保存目录</summary>
    public string SaveDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "WinDrop");

    /// <summary>是否自动将 iPhone 发来的 HEIC 图片转为 JPG</summary>
    public bool ConvertHeicToJpg { get; set; } = false;

    /// <summary>转换 HEIC 后是否保留原文件</summary>
    public bool KeepOriginalHeic { get; set; } = true;

    /// <summary>开机静默自启</summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>资源管理器右键「通过 WinDrop 分享」开关</summary>
    public bool EnableContextMenu { get; set; } = true;

    /// <summary>是否启用向 iPhone 发送主动推送通知（右键分享或热键推送时触发）</summary>
    public bool EnablePushNotification { get; set; } = true;

    /// <summary>Bark 推送 Key 或完整服务器地址（支持 https://api.day.app/xxx 或自建服务器）</summary>
    public string BarkKeyOrUrl { get; set; } = string.Empty;

    /// <summary>点击 iPhone 推送横幅时自动唤起的快捷指令名称</summary>
    public string ReceiveShortcutName { get; set; } = "AirDrop Plus";

    /// <summary>推送文本/网址剪贴板时是否允许在通知中附带内容（开启后支持点击直开网址或下拉一键复制）</summary>
    public bool PushIncludeClipboardText { get; set; } = false;

    /// <summary>可选：已签名的「发送到电脑」专属 iCloud 快捷指令链接</summary>
    public string CustomSendShortcutUrl { get; set; } = string.Empty;

    /// <summary>可选：已签名的「从电脑接收」专属 iCloud 快捷指令链接</summary>
    public string CustomReceiveShortcutUrl { get; set; } = string.Empty;

    /// <summary>兼容的快捷指令主次版本号（默认 1.5，兼容 AirDropPlus 官方签名快捷指令 v1.5.x）</summary>
    public string Version { get; set; } = "1.5.3";
}

/// <summary>
/// 剪贴板内容类型枚举（严格对应 iOS 快捷指令解析分支：file / img / text）
/// </summary>
public enum ClipboardDataType
{
    Empty,
    File,
    Image,
    Text
}

/// <summary>
/// 电脑剪贴板或右键分享槽位快照
/// </summary>
public sealed class ClipboardSnapshot
{
    public ClipboardDataType Type { get; init; } = ClipboardDataType.Empty;
    public string? Text { get; init; }
    public List<string>? FilePaths { get; init; }
    public string? ImagePngBase64 { get; init; }

    public static ClipboardSnapshot Empty => new() { Type = ClipboardDataType.Empty };
}

/// <summary>
/// 统一 JSON 响应格式：{ "success": bool, "msg": string, "data": object? }
/// 100% 兼容 Apple 官方 iCloud 签名快捷指令解析结构
/// </summary>
public sealed class ApiResult
{
    public bool Success { get; init; }
    public string Msg { get; init; } = string.Empty;
    public object? Data { get; init; }

    public static ApiResult Ok(string msg = "发送成功", object? data = null) =>
        new() { Success = true, Msg = msg, Data = data };

    public static ApiResult Fail(string msg) =>
        new() { Success = false, Msg = msg, Data = null };
}

/// <summary>
/// 收发通知事件参数（驱动 Windows 右下角可交互通知窗）
/// </summary>
public sealed class TransferNotificationEvent
{
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    /// <summary>若是收到的本地文件，提供完整路径以支持通知栏一键打开/定位/复制</summary>
    public string? SavedFilePath { get; init; }
    public bool IsError { get; init; }
}

/// <summary>
/// v2 零菜单拆分版快捷指令内容分类常量
/// </summary>
public static class V2ContentCategory
{
    public const string Image = "image";
    public const string Video = "video";
    public const string File = "file";
    public const string Url = "url";
    public const string Text = "text";
    public const string Empty = "empty";
}

/// <summary>
/// v2 接收接口单个待下载文件描述
/// </summary>
public sealed class V2ReceiveFileItem
{
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = V2ContentCategory.File;
    public string Url { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
}

/// <summary>
/// v2 接收接口统一响应结构（GET /v2/receive）
/// </summary>
public sealed class V2ReceiveResponse
{
    public bool Success { get; init; }
    public string Category { get; init; } = V2ContentCategory.Empty;
    public string Summary { get; init; } = string.Empty;
    public string? Text { get; init; }
    public List<V2ReceiveFileItem> Files { get; init; } = new();
}
