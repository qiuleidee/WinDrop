using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using WinDrop.Core.Models;
using WinDrop.Core.Network;
using WinDrop.Core.Security;
using WinDrop.Core.Services;

namespace WinDrop.Core.Server;

/// <summary>
/// WinDrop 核心 HTTP 流式传输与剪贴板互通服务
/// 100% 兼容 Apple 官方 iCloud 签名快捷指令接口协议（/、/file、/upload、/file/{**encodedPath}、/clipboard、/setup）
/// </summary>
public sealed class WinDropServer : IAsyncDisposable
{
    private readonly WinDropStateService _state;
    private readonly IClipboardProvider _clipboard;
    private readonly IHeicConverter _heicConverter;
    private readonly IPushNotificationService _pushService;
    private readonly bool _enableMdns;

    private WebApplication? _app;
    private MdnsResponder? _mdns;

    public int ListeningPort { get; private set; }
    public string PrimaryMdnsDomain => _mdns?.PrimaryDomain ?? $"{Dns.GetHostName()}.local";
    public IPAddress LocalIp => NetworkHelper.GetPrimaryLocalIPv4();
    public WinDropStateService State => _state;
    public IPushNotificationService PushService => _pushService;

    public WinDropServer(
        WinDropStateService state,
        IClipboardProvider? clipboard = null,
        IHeicConverter? heicConverter = null,
        IPushNotificationService? pushService = null,
        bool enableMdns = true)
    {
        _state = state;
        _clipboard = clipboard ?? new InMemoryClipboardProvider();
        _heicConverter = heicConverter ?? new NullHeicConverter();
        _pushService = pushService ?? new BarkPushService();
        _enableMdns = enableMdns;
    }

    public async Task StartAsync(int? overridePort = null, bool bindLoopbackOnly = false, CancellationToken cancellationToken = default)
    {
        if (_app != null)
            return;

        ListeningPort = overridePort ?? NetworkHelper.FindAvailablePort(_state.Config.Port);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(options =>
        {
            // 无大小上限，支持数 GB 的 4K 视频原画质流式直传
            options.Limits.MaxRequestBodySize = null;
            options.Limits.MinRequestBodyDataRate = null;
            if (bindLoopbackOnly)
                options.Listen(IPAddress.Loopback, ListeningPort);
            else
                options.ListenAnyIP(ListeningPort);
        });

        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = long.MaxValue;
            options.ValueLengthLimit = int.MaxValue;
            options.MemoryBufferThreshold = 64 * 1024;
        });

        var app = builder.Build();

        // 统一密钥校验中间件（放行 /、/setup、/icon.svg 以及自带高熵短 Ticket 鉴权的 /v2/dl/）
        app.Use(async (ctx, next) =>
        {
            string path = ctx.Request.Path.Value ?? "/";
            if (path == "/" ||
                path.Equals("/setup", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("/icon.svg", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/v2/dl/", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            if (!ValidateApiKey(ctx))
            {
                _state.Notify("⚠️ 访问被拦截", "手机端快捷指令密钥（Key）不匹配，请核对设置。", isError: true);
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(ApiResult.Fail("Key验证失败"));
                return;
            }

            await next();
        });

        // 1. GET / —— 快捷指令连通性测试接口
        app.MapGet("/", () =>
        {
            _state.Notify("✅ 连接测试成功", "iPhone 已成功连通本台 Windows 电脑！");
            return Results.Text("Hello World!", "text/plain; charset=utf-8");
        });

        // 2. GET /setup —— iPhone 扫码一键安装成品快捷指令与一键复制配置参数页
        app.MapGet("/setup", (HttpContext ctx) =>
        {
            string html = BuildMobileSetupHtml(ctx);
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // 3. v1 兼容路由（适配 AirDropPlus 1.5.3 官方签名快捷指令）
        app.MapPost("/file", (Func<HttpContext, Task<IResult>>)HandleFileUploadAsync);
        app.MapPost("/upload", (Func<HttpContext, Task<IResult>>)HandleFileUploadAsync);
        app.MapGet("/clipboard", (Func<Task<IResult>>)HandleGetClipboardAsync);
        app.MapPost("/clipboard", (Func<HttpContext, Task<IResult>>)HandleSetClipboardAsync);
        app.MapGet("/file/{**encodedPath}", (Func<string?, IResult>)HandleDownloadSharedFileAsync);

        // 4. v2 零菜单拆分专用路由（升级项 #2：极简直达 + 智能分类归档）
        app.MapPost("/v2/send", (Func<HttpContext, Task<IResult>>)HandleV2SendAsync);
        app.MapGet("/v2/receive", (Func<HttpContext, Task<IResult>>)HandleV2ReceiveAsync);
        app.MapGet("/v2/dl/{ticket}", (Func<string?, IResult>)HandleV2DownloadByTicket);

        await app.StartAsync(cancellationToken);
        _app = app;

        if (_enableMdns && !bindLoopbackOnly)
        {
            _mdns = new MdnsResponder(ListeningPort);
            await _mdns.StartAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 将资源管理器「右键分享」选中的文件列表直接推入系统剪贴板并加入授权白名单
    /// </summary>
    public async Task<int> ShareFilesFromContextMenuAsync(IEnumerable<string> paths)
    {
        var expandedFiles = new List<string>();
        foreach (string p in paths)
        {
            if (File.Exists(p))
            {
                expandedFiles.Add(Path.GetFullPath(p));
            }
            else if (Directory.Exists(p))
            {
                expandedFiles.AddRange(Directory.EnumerateFiles(p).Select(Path.GetFullPath));
            }
        }

        if (expandedFiles.Count == 0)
            return 0;

        _state.AuthorizeFilesForDownload(expandedFiles);
        await _clipboard.SetFilesAsync(expandedFiles);

        string summary = expandedFiles.Count == 1
            ? Path.GetFileName(expandedFiles[0])
            : $"{Path.GetFileName(expandedFiles[0])} 等共 {expandedFiles.Count} 个文件";

        if (_state.Config.EnablePushNotification && !string.IsNullOrWhiteSpace(_state.Config.BarkKeyOrUrl))
        {
            var (pushed, _) = await _pushService.SendShortcutWakePushAsync(
                _state.Config,
                $"💻 {Dns.GetHostName()} 发来文件",
                $"{summary} · 轻点通知立即接收");

            if (pushed)
            {
                _state.Notify("📲 已通知 iPhone 弹窗接收", $"{summary}\n轻点手机通知横幅即可自动拉取");
                return expandedFiles.Count;
            }
        }

        _state.Notify("📤 已就绪等待 iPhone 接收", $"已准备：{summary}\n请在 iPhone 运行快捷指令接收");
        return expandedFiles.Count;
    }

    /// <summary>
    /// 主动将当前电脑剪贴板内容（文件 / 截图 / 文本 / 网址）推送通知至 iPhone，手机点击横幅即可一键接收
    /// </summary>
    public async Task<(bool Success, string Message)> PushCurrentClipboardToPhoneAsync(CancellationToken ct = default)
    {
        var snapshot = await _clipboard.GetClipboardSnapshotAsync();
        string host = Dns.GetHostName();

        switch (snapshot.Type)
        {
            case ClipboardDataType.Empty:
                _state.Notify("⚠️ 当前电脑剪贴板为空", "请先复制文件、截图或文字后再推送", isError: true);
                return (false, "当前电脑剪贴板为空");

            case ClipboardDataType.File when snapshot.FilePaths is { Count: > 0 }:
            {
                _state.AuthorizeFilesForDownload(snapshot.FilePaths);
                string summary = snapshot.FilePaths.Count == 1
                    ? Path.GetFileName(snapshot.FilePaths[0])
                    : $"{Path.GetFileName(snapshot.FilePaths[0])} 等共 {snapshot.FilePaths.Count} 个文件";

                var (ok, msg) = await _pushService.SendShortcutWakePushAsync(
                    _state.Config,
                    $"💻 {host} 发来文件",
                    $"{summary} · 轻点通知立即接收",
                    cancellationToken: ct);

                _state.Notify(
                    ok ? "📲 已通知 iPhone 接收文件" : "⚠️ 推送提醒未送达",
                    ok ? $"{summary}\n请轻点 iPhone 顶部通知" : $"{msg}（仍可直接运行快捷指令接收）",
                    isError: !ok);
                return (ok, msg);
            }

            case ClipboardDataType.Image when !string.IsNullOrEmpty(snapshot.ImagePngBase64):
            {
                var (ok, msg) = await _pushService.SendShortcutWakePushAsync(
                    _state.Config,
                    $"🏞️ {host} 发来剪贴板图片",
                    "轻点通知立即接收截图",
                    cancellationToken: ct);

                _state.Notify(
                    ok ? "📲 已通知 iPhone 接收截图" : "⚠️ 推送提醒未送达",
                    ok ? "请轻点 iPhone 顶部通知接收图片" : $"{msg}（仍可直接运行快捷指令接收）",
                    isError: !ok);
                return (ok, msg);
            }

            case ClipboardDataType.Text when !string.IsNullOrEmpty(snapshot.Text):
            {
                string raw = snapshot.Text;
                bool isUrl = Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) &&
                             (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                              uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));

                string title = isUrl ? $"🔗 {host} 发来网页链接" : $"📝 {host} 发来剪贴板文本";
                string body;
                string? urlOverride = null;
                string? copyOverride = null;

                if (_state.Config.PushIncludeClipboardText)
                {
                    copyOverride = raw;
                    if (isUrl)
                    {
                        urlOverride = raw.Trim();
                        body = $"{TruncatePreview(raw)}（轻点直开网页，长按可复制）";
                    }
                    else
                    {
                        body = raw.Length > 60 ? raw[..60] + "…" : raw;
                    }
                }
                else
                {
                    body = "轻点通知通过局域网拉取剪贴板文本";
                }

                var (ok, msg) = await _pushService.SendShortcutWakePushAsync(
                    _state.Config,
                    title,
                    body,
                    directUrlOverride: urlOverride,
                    copyTextOverride: copyOverride,
                    cancellationToken: ct);

                _state.Notify(
                    ok ? "📲 已推送剪贴板通知至 iPhone" : "⚠️ 推送提醒未送达",
                    ok ? "iPhone 已弹窗提醒接收" : $"{msg}（仍可直接运行快捷指令接收）",
                    isError: !ok);
                return (ok, msg);
            }

            default:
                return (false, "当前电脑剪贴板无有效可分享内容");
        }
    }

    private sealed record IncomingUploadResult(List<string> SavedPaths, string? SavedText);

    private async Task<IResult> HandleFileUploadAsync(HttpContext ctx)
    {
        try
        {
            var result = await SaveIncomingPayloadAsync(ctx);
            if (!string.IsNullOrEmpty(result.SavedText) && result.SavedPaths.Count == 0)
            {
                await _clipboard.SetTextAsync(result.SavedText);
                _state.Notify("📥 已收到手机分享文本（已复制）", TruncatePreview(result.SavedText));
                return Results.Json(ApiResult.Ok("发送成功"));
            }

            if (result.SavedPaths.Count == 0)
            {
                return Results.Json(ApiResult.Fail("未检测到上传的文件内容"));
            }

            string lastFile = result.SavedPaths[^1];
            string displayMsg = result.SavedPaths.Count == 1
                ? Path.GetFileName(lastFile)
                : $"{Path.GetFileName(result.SavedPaths[0])} 等 {result.SavedPaths.Count} 个文件已保存";

            _state.Notify("📥 收到 iPhone 分享文件", displayMsg, savedFilePath: lastFile);
            return Results.Json(ApiResult.Ok("发送成功"));
        }
        catch (Exception ex)
        {
            _state.Notify("⚠️ 接收文件失败", ex.Message, isError: true);
            return Results.Json(ApiResult.Fail($"上传失败: {ex.Message}"), statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private async Task<IResult> HandleV2SendAsync(HttpContext ctx)
    {
        bool wantsJson = string.Equals(ctx.Request.Query["format"], "json", StringComparison.OrdinalIgnoreCase) ||
                         ctx.Request.Headers.Accept.Any(a => a != null && a.Contains("application/json", StringComparison.OrdinalIgnoreCase));

        try
        {
            var result = await SaveIncomingPayloadAsync(ctx);
            if (!string.IsNullOrEmpty(result.SavedText) && result.SavedPaths.Count == 0)
            {
                await _clipboard.SetTextAsync(result.SavedText);
                string textNotice = "✅ 已复制手机分享的文本到电脑剪贴板";
                _state.Notify("📥 已收到手机分享文本（已复制）", TruncatePreview(result.SavedText));
                return wantsJson
                    ? Results.Json(ApiResult.Ok(textNotice, new { category = V2ContentCategory.Text }))
                    : Results.Text(textNotice, "text/plain; charset=utf-8");
            }

            if (result.SavedPaths.Count == 0)
            {
                return wantsJson
                    ? Results.Json(ApiResult.Fail("未检测到上传内容"), statusCode: StatusCodes.Status400BadRequest)
                    : Results.Text("⚠️ 未检测到发送内容", "text/plain; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);
            }

            string lastFile = result.SavedPaths[^1];
            string displayMsg = result.SavedPaths.Count == 1
                ? Path.GetFileName(lastFile)
                : $"{Path.GetFileName(result.SavedPaths[0])} 等 {result.SavedPaths.Count} 个文件已保存";
            string summaryText = $"✅ 已保存 {result.SavedPaths.Count} 个文件到 {Dns.GetHostName()}";

            _state.Notify("📥 收到 iPhone 分享文件", displayMsg, savedFilePath: lastFile);
            return wantsJson
                ? Results.Json(ApiResult.Ok(summaryText, new
                {
                    count = result.SavedPaths.Count,
                    files = result.SavedPaths.Select(Path.GetFileName).ToList()
                }))
                : Results.Text(summaryText, "text/plain; charset=utf-8");
        }
        catch (Exception ex)
        {
            _state.Notify("⚠️ 接收文件失败", ex.Message, isError: true);
            return wantsJson
                ? Results.Json(ApiResult.Fail($"上传失败: {ex.Message}"), statusCode: StatusCodes.Status500InternalServerError)
                : Results.Text($"⚠️ 发送失败：{ex.Message}", "text/plain; charset=utf-8", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private async Task<IncomingUploadResult> SaveIncomingPayloadAsync(HttpContext ctx)
    {
        string? contentType = ctx.Request.ContentType;
        var savedPaths = new List<string>();
        string? savedText = null;

        if (!string.IsNullOrWhiteSpace(contentType) &&
            contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            string? boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(contentType).Boundary).Value;
            if (string.IsNullOrEmpty(boundary))
            {
                throw new InvalidDataException("缺少 Multipart 边界标识");
            }

            var reader = new MultipartReader(boundary, ctx.Request.Body);
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ctx.RequestAborted)) != null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd))
                    continue;

                string? rawFileName = HeaderUtilities.RemoveQuotes(cd.FileNameStar).Value;
                if (string.IsNullOrWhiteSpace(rawFileName))
                    rawFileName = HeaderUtilities.RemoveQuotes(cd.FileName).Value;

                if (string.IsNullOrWhiteSpace(rawFileName))
                {
                    using var sr = new StreamReader(section.Body, Encoding.UTF8);
                    string formText = (await sr.ReadToEndAsync(ctx.RequestAborted)).Trim();
                    if (!string.IsNullOrEmpty(formText))
                    {
                        savedText = formText;
                    }
                    continue;
                }

                string saved = await StreamToSafeFileAsync(section.Body, rawFileName, ctx.RequestAborted);
                savedPaths.Add(saved);
            }
        }
        else if (!string.IsNullOrWhiteSpace(contentType) &&
                 contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) &&
                 string.IsNullOrWhiteSpace(ctx.Request.Headers["X-File-Name"].FirstOrDefault()))
        {
            using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            string textBody = (await sr.ReadToEndAsync(ctx.RequestAborted)).Trim();
            if (!string.IsNullOrEmpty(textBody))
            {
                savedText = textBody;
            }
        }
        else
        {
            string rawName = ctx.Request.Headers["X-File-Name"].FirstOrDefault()
                             ?? ctx.Request.Query["filename"].FirstOrDefault()
                             ?? GenerateDefaultNameFromContentType(contentType);
            string saved = await StreamToSafeFileAsync(ctx.Request.Body, rawName, ctx.RequestAborted);
            savedPaths.Add(saved);
        }

        return new IncomingUploadResult(savedPaths, savedText);
    }

    private static string GenerateDefaultNameFromContentType(string? contentType)
    {
        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        if (string.IsNullOrWhiteSpace(contentType))
            return $"iPhone_{ts}.dat";

        string ct = contentType.Split(';')[0].Trim().ToLowerInvariant();
        string ext = ct switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/heic" => ".heic",
            "image/heif" => ".heif",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "video/quicktime" => ".mov",
            "video/mp4" => ".mp4",
            "application/pdf" => ".pdf",
            "application/zip" => ".zip",
            "text/plain" => ".txt",
            _ => ".dat"
        };
        return $"iPhone_{ts}{ext}";
    }

    private async Task<string> StreamToSafeFileAsync(Stream inputStream, string rawFileName, CancellationToken ct)
    {
        Directory.CreateDirectory(_state.StagingDirectory);
        string stagingPart = Path.Combine(_state.StagingDirectory, $"{Guid.NewGuid():N}.part");

        try
        {
            byte[] buffer = new byte[128 * 1024];
            await using (var fs = new FileStream(stagingPart, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                int read;
                while ((read = await inputStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                await fs.FlushAsync(ct);
            }

            string finalPath = FileNameSanitizer.ResolveUniquePath(_state.Config.SaveDirectory, rawFileName);
            File.Move(stagingPart, finalPath, overwrite: false);

            // 可选自动将 HEIC 转为 JPG
            string ext = Path.GetExtension(finalPath).ToLowerInvariant();
            if (_state.Config.ConvertHeicToJpg && ext is ".heic" or ".heif")
            {
                string? jpg = await _heicConverter.TryConvertHeicToJpgAsync(finalPath, _state.Config.KeepOriginalHeic);
                if (!string.IsNullOrEmpty(jpg) && File.Exists(jpg))
                {
                    finalPath = jpg;
                }
            }

            return finalPath;
        }
        catch
        {
            if (File.Exists(stagingPart))
            {
                try { File.Delete(stagingPart); } catch { }
            }
            throw;
        }
    }

    private async Task<IResult> HandleGetClipboardAsync()
    {
        var snapshot = await _clipboard.GetClipboardSnapshotAsync();

        switch (snapshot.Type)
        {
            case ClipboardDataType.File when snapshot.FilePaths is { Count: > 0 }:
            {
                _state.AuthorizeFilesForDownload(snapshot.FilePaths);
                var encodedList = snapshot.FilePaths.Select(QrAndPathHelper.EncodeFilePathBase64).ToList();
                string summary = snapshot.FilePaths.Count == 1
                    ? Path.GetFileName(snapshot.FilePaths[0])
                    : $"共 {snapshot.FilePaths.Count} 个文件";
                _state.Notify("📤 正在向 iPhone 发送文件列表", summary);
                return Results.Json(ApiResult.Ok(string.Empty, new
                {
                    type = "file",
                    data = encodedList
                }));
            }

            case ClipboardDataType.Image when !string.IsNullOrEmpty(snapshot.ImagePngBase64):
            {
                _state.Notify("🏞️ 正在向 iPhone 发送剪贴板图片", "手机端即将收到截图");
                return Results.Json(ApiResult.Ok(string.Empty, new
                {
                    type = "img",
                    data = snapshot.ImagePngBase64
                }));
            }

            case ClipboardDataType.Text when !string.IsNullOrEmpty(snapshot.Text):
            {
                _state.Notify("📤 已发送电脑剪贴板文本", TruncatePreview(snapshot.Text));
                return Results.Json(ApiResult.Ok(string.Empty, new
                {
                    type = "text",
                    data = snapshot.Text
                }));
            }

            default:
                _state.Notify("⚠️ 电脑剪贴板为空", "请先在电脑复制文字/图片/文件，或右键文件选择通过 WinDrop 分享", isError: true);
                return Results.Json(ApiResult.Fail("Windows剪贴板为空"));
        }
    }

    private async Task<IResult> HandleSetClipboardAsync(HttpContext ctx)
    {
        string? text = null;

        if (ctx.Request.HasFormContentType)
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            text = form["clipboard"].FirstOrDefault() ?? form["content"].FirstOrDefault();
        }
        else
        {
            using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            string rawBody = await sr.ReadToEndAsync(ctx.RequestAborted);
            if (!string.IsNullOrWhiteSpace(rawBody))
            {
                string trimmed = rawBody.Trim();
                if (trimmed.StartsWith('{'))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(trimmed);
                        if (doc.RootElement.TryGetProperty("clipboard", out var cp))
                            text = cp.GetString();
                        else if (doc.RootElement.TryGetProperty("content", out var ct))
                            text = ct.GetString();
                    }
                    catch
                    {
                        text = rawBody;
                    }
                }
                else
                {
                    text = rawBody;
                }
            }
        }

        if (string.IsNullOrEmpty(text))
        {
            return Results.Json(ApiResult.Fail("剪贴板内容为空"));
        }

        bool ok = await _clipboard.SetTextAsync(text);
        if (!ok)
        {
            return Results.Json(ApiResult.Fail("设置 Windows 剪贴板失败"));
        }

        _state.Notify("📥 收到 iPhone 剪贴板文本", TruncatePreview(text));
        return Results.Json(ApiResult.Ok("发送成功"));
    }

    private IResult HandleDownloadSharedFileAsync(string? encodedPath)
    {
        string? decodedPath = QrAndPathHelper.DecodeFilePathBase64(encodedPath ?? string.Empty);
        if (string.IsNullOrEmpty(decodedPath) || !_state.IsFileAuthorizedForDownload(decodedPath, out string fullPath))
        {
            _state.Notify("⚠️ 文件读取被拒绝", "文件路径无效或未经电脑端复制/右键分享授权", isError: true);
            return Results.NotFound(ApiResult.Fail("文件不存在或未授权分享"));
        }

        return StreamAuthorizedFile(fullPath);
    }

    private IResult HandleV2DownloadByTicket(string? ticket)
    {
        if (!_state.TryResolveDownloadTicket(ticket, out string fullPath))
        {
            return Results.NotFound(ApiResult.Fail("下载凭证无效或已过期"));
        }

        return StreamAuthorizedFile(fullPath);
    }

    private IResult StreamAuthorizedFile(string fullPath)
    {
        string fileName = Path.GetFileName(fullPath);
        string mime = WinDropStateService.GuessMimeType(fileName);
        _state.Notify("📤 正在传输文件到 iPhone", fileName);

        // 纯流式异步读取发送（零整文件内存缓冲，支持 10GB 大视频与 Range 请求）
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        return Results.File(stream, contentType: mime, fileDownloadName: fileName, enableRangeProcessing: true);
    }

    private async Task<IResult> HandleV2ReceiveAsync(HttpContext ctx)
    {
        bool directMode = string.Equals(ctx.Request.Query["direct"], "1", StringComparison.Ordinal);
        string baseHost = ctx.Request.Host.HasValue ? $"http://{ctx.Request.Host.Value}" : $"http://{LocalIp}:{ListeningPort}";
        var snapshot = await _clipboard.GetClipboardSnapshotAsync();

        switch (snapshot.Type)
        {
            case ClipboardDataType.File when snapshot.FilePaths is { Count: > 0 }:
            {
                var items = new List<V2ReceiveFileItem>();
                foreach (string fp in snapshot.FilePaths)
                {
                    if (!File.Exists(fp))
                        continue;
                    string ticket = _state.IssueDownloadTicket(fp);
                    string fileName = Path.GetFileName(fp);
                    items.Add(new V2ReceiveFileItem
                    {
                        Name = fileName,
                        Category = WinDropStateService.ClassifyFileCategory(fileName),
                        Url = $"{baseHost}/v2/dl/{ticket}",
                        SizeBytes = new FileInfo(fp).Length
                    });
                }

                if (items.Count == 0)
                {
                    return directMode
                        ? Results.NotFound("电脑待分享文件不存在")
                        : Results.Json(new V2ReceiveResponse { Success = false, Category = V2ContentCategory.Empty, Summary = "电脑剪贴板为空" });
                }

                if (directMode)
                {
                    string firstPath = snapshot.FilePaths.First(File.Exists);
                    ctx.Response.Headers["X-WinDrop-Category"] = items[0].Category;
                    return StreamAuthorizedFile(firstPath);
                }

                string overallCategory = items.All(i => i.Category == V2ContentCategory.Image)
                    ? V2ContentCategory.Image
                    : items.All(i => i.Category == V2ContentCategory.Video)
                        ? V2ContentCategory.Video
                        : V2ContentCategory.File;

                string summary = items.Count == 1
                    ? items[0].Name
                    : $"{items[0].Name} 等共 {items.Count} 个文件";
                _state.Notify("📤 已向 iPhone 下发文件清单", summary);

                return Results.Json(new V2ReceiveResponse
                {
                    Success = true,
                    Category = overallCategory,
                    Summary = summary,
                    Files = items
                });
            }

            case ClipboardDataType.Image when !string.IsNullOrEmpty(snapshot.ImagePngBase64):
            {
                string outDir = Path.Combine(_state.StagingDirectory, "ClipboardOut");
                Directory.CreateDirectory(outDir);
                string fileName = $"剪贴板截图_{DateTime.Now:HHmmss}_{Guid.NewGuid():N}"[..20] + ".png";
                string tempPngPath = Path.Combine(outDir, fileName);
                await File.WriteAllBytesAsync(tempPngPath, Convert.FromBase64String(snapshot.ImagePngBase64), ctx.RequestAborted);

                string ticket = _state.IssueDownloadTicket(tempPngPath);
                if (directMode)
                {
                    ctx.Response.Headers["X-WinDrop-Category"] = V2ContentCategory.Image;
                    return StreamAuthorizedFile(tempPngPath);
                }

                _state.Notify("🏞️ 已向 iPhone 下发剪贴板截图", fileName);
                return Results.Json(new V2ReceiveResponse
                {
                    Success = true,
                    Category = V2ContentCategory.Image,
                    Summary = fileName,
                    Files = new List<V2ReceiveFileItem>
                    {
                        new()
                        {
                            Name = fileName,
                            Category = V2ContentCategory.Image,
                            Url = $"{baseHost}/v2/dl/{ticket}",
                            SizeBytes = new FileInfo(tempPngPath).Length
                        }
                    }
                });
            }

            case ClipboardDataType.Text when !string.IsNullOrEmpty(snapshot.Text):
            {
                string raw = snapshot.Text;
                bool isUrl = Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) &&
                             (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                              uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));
                string category = isUrl ? V2ContentCategory.Url : V2ContentCategory.Text;

                _state.Notify(isUrl ? "🔗 已向 iPhone 下发网址" : "📤 已向 iPhone 下发剪贴板文本", TruncatePreview(raw));

                if (directMode)
                {
                    ctx.Response.Headers["X-WinDrop-Category"] = category;
                    return Results.Text(raw, "text/plain; charset=utf-8");
                }

                return Results.Json(new V2ReceiveResponse
                {
                    Success = true,
                    Category = category,
                    Summary = TruncatePreview(raw),
                    Text = isUrl ? raw.Trim() : raw
                });
            }

            default:
                if (directMode)
                    return Results.NotFound("电脑剪贴板为空");
                return Results.Json(new V2ReceiveResponse
                {
                    Success = false,
                    Category = V2ContentCategory.Empty,
                    Summary = "电脑剪贴板为空"
                });
        }
    }

    private bool ValidateApiKey(HttpContext ctx)
    {
        string configuredKey = _state.Config.Key ?? string.Empty;
        if (string.IsNullOrEmpty(configuredKey))
        {
            return true; // 未设置密钥时允许局域网免密直连
        }

        string? provided = ctx.Request.Headers.Authorization.FirstOrDefault()
                           ?? ctx.Request.Headers["X-WinDrop-Token"].FirstOrDefault()
                           ?? ctx.Request.Query["key"].FirstOrDefault();

        if (string.IsNullOrEmpty(provided))
            return false;

        string cleaned = provided.Trim();
        if (cleaned.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned["Bearer ".Length..].Trim();
        }

        byte[] a = Encoding.UTF8.GetBytes(configuredKey);
        byte[] b = Encoding.UTF8.GetBytes(cleaned);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private string BuildMobileSetupHtml(HttpContext ctx)
    {
        string mdns = System.Net.WebUtility.HtmlEncode(PrimaryMdnsDomain);
        string ip = System.Net.WebUtility.HtmlEncode(LocalIp.ToString());
        int port = ListeningPort;
        string key = System.Net.WebUtility.HtmlEncode(_state.Config.Key);
        string shortcutUrl = QrAndPathHelper.OfficialSignedShortcutUrl;

        string activeBase = ctx.Request.Host.HasValue ? $"http://{ctx.Request.Host.Value}" : $"http://{PrimaryMdnsDomain}:{port}";
        string v2SendUrl = System.Net.WebUtility.HtmlEncode($"{activeBase}/v2/send");
        string v2ReceiveUrl = System.Net.WebUtility.HtmlEncode($"{activeBase}/v2/receive");

        string splitInstallHtml = string.Empty;
        if (!string.IsNullOrWhiteSpace(_state.Config.CustomSendShortcutUrl) ||
            !string.IsNullOrWhiteSpace(_state.Config.CustomReceiveShortcutUrl))
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(_state.Config.CustomSendShortcutUrl))
            {
                string u = System.Net.WebUtility.HtmlEncode(_state.Config.CustomSendShortcutUrl);
                sb.AppendLine($"<a class=\"btn\" style=\"margin-bottom:10px;\" href=\"{u}\">📤 一键安装【发送到电脑】（专属拆分版）</a>");
            }
            if (!string.IsNullOrWhiteSpace(_state.Config.CustomReceiveShortcutUrl))
            {
                string u = System.Net.WebUtility.HtmlEncode(_state.Config.CustomReceiveShortcutUrl);
                sb.AppendLine($"<a class=\"btn\" style=\"background:#059669;margin-bottom:10px;\" href=\"{u}\">📥 一键安装【从电脑接收】（专属拆分版）</a>");
            }
            splitInstallHtml = sb.ToString();
        }

        return $$"""
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
              <meta charset="UTF-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no" />
              <title>WinDrop 快捷指令一键安装</title>
              <style>
                body { font-family: -apple-system, BlinkMacSystemFont, "PingFang SC", sans-serif; background: #0f172a; color: #f8fafc; margin: 0; padding: 20px 16px 40px; }
                .card { background: #1e293b; border-radius: 18px; padding: 20px; margin-bottom: 16px; border: 1px solid rgba(148,163,184,0.2); }
                h1 { font-size: 20px; margin: 0 0 8px; }
                h2 { font-size: 16px; margin: 0 0 12px; color: #60a5fa; }
                p { font-size: 13px; color: #94a3b8; line-height: 1.6; margin: 0 0 14px; }
                .btn { display: block; text-align: center; background: #2563eb; color: #fff; text-decoration: none; font-weight: 700; font-size: 15px; padding: 13px; border-radius: 14px; border: none; width: 100%; box-sizing: border-box; }
                .row { display: flex; align-items: center; justify-content: space-between; gap: 10px; background: #0f172a; padding: 11px 14px; border-radius: 12px; margin-bottom: 10px; border: 1px solid rgba(148,163,184,0.18); }
                .lbl { font-size: 12px; color: #94a3b8; }
                .val { font-family: ui-monospace, Menlo, monospace; font-size: 13.5px; font-weight: 700; color: #38bdf8; word-break: break-all; }
                .copy-btn { background: #334155; color: #fff; border: none; border-radius: 8px; padding: 6px 12px; font-size: 12px; font-weight: 600; flex-shrink: 0; }
                .tip { font-size: 12.5px; color: #cbd5e1; line-height: 1.65; }
              </style>
            </head>
            <body>
              <div class="card">
                <h1>📲 WinDrop 成品快捷指令安装</h1>
                <p>Apple 官方 iCloud 已签名快捷指令，无需手动编写任何步骤。安装后自动支持「共享表单一键发到电脑」与「一键接收电脑复制/右键分享的文件」。</p>
                {{splitInstallHtml}}
                <a class="btn" href="{{shortcutUrl}}">第 1 步：点击获取并添加官方快捷指令（一体版）</a>
              </div>

              <div class="card">
                <h2>第 2 步：首次添加时填入以下 3 项参数</h2>
                <div class="row">
                  <div><div class="lbl">主机域名 (host · 推荐填此项，换 Wi-Fi 不失效)</div><div class="val" id="vHost">{{mdns}}</div></div>
                  <button class="copy-btn" onclick="cp('vHost')">复制</button>
                </div>
                <div class="row">
                  <div><div class="lbl">备用局域网 IP (若路由器不支持 .local 时填写)</div><div class="val" id="vIp">{{ip}}</div></div>
                  <button class="copy-btn" onclick="cp('vIp')">复制</button>
                </div>
                <div class="row">
                  <div><div class="lbl">端口 (port)</div><div class="val" id="vPort">{{port}}</div></div>
                  <button class="copy-btn" onclick="cp('vPort')">复制</button>
                </div>
                <div class="row">
                  <div><div class="lbl">密钥 (key)</div><div class="val" id="vKey">{{key}}</div></div>
                  <button class="copy-btn" onclick="cp('vKey')">复制</button>
                </div>
              </div>

              <div class="card">
                <h2>⚡ 进阶：零菜单拆分版快捷指令（无弹窗 · 智能存相册）</h2>
                <p>如果你希望把「发到电脑」与「从电脑接收」完全拆开（点接收再也不弹 Send/Receive 选择菜单，且照片视频自动存入系统相册、文档自动存文件、网址直开 Safari），使用以下 v2 专线接口即可：</p>
                <div class="row">
                  <div><div class="lbl">发送专线接口 (POST · 表单字段 file)</div><div class="val" id="v2Send">{{v2SendUrl}}</div></div>
                  <button class="copy-btn" onclick="cp('v2Send')">复制</button>
                </div>
                <div class="row">
                  <div><div class="lbl">接收专线接口 (GET · 加 ?direct=1 可直接拿单文件)</div><div class="val" id="v2Recv">{{v2ReceiveUrl}}</div></div>
                  <button class="copy-btn" onclick="cp('v2Recv')">复制</button>
                </div>
                <div class="tip">
                  <b>三步配置极简版：</b><br/>
                  • <b>发到电脑</b>：开启「在共享表单显示」→ 动作「获取 URL 内容」→ POST 上方发送专线 → 头部填 <code>Authorization: {{key}}</code> → 请求体选表单，字段 <code>file</code> = 快捷指令输入。<br/>
                  • <b>从电脑接收</b>：可绑定「操作按钮 / 轻点背面 / Bark 唤醒名称设为『从电脑接收』」→ GET 上方接收专线，支持一键解析 <code>category</code>（image/video 存相簿、file 存文件、url 打开、text 拷剪贴板）。
                </div>
              </div>

              <div class="card tip">
                <b>💡 日常使用秘籍：</b><br/>
                1. <b>手机发电脑</b>：在相册/文件点「分享」→ 选择该快捷指令，自动秒传到电脑接收目录。<br/>
                2. <b>电脑发手机</b>：在电脑对文件按 <b>Ctrl+C 复制</b> 或 <b>右键 → 通过 WinDrop 分享</b>，然后在手机点击接收（或轻点 Bark 推送横幅）即可直接接收！<br/>
                3. <b>多张原图必开设置</b>：首次传多图前，请前往 iPhone「设置 → App → 快捷指令 → 高级 → 开启『允许共享大量数据』」。
              </div>

              <script>
                async function cp(id) {
                  const t = document.getElementById(id).textContent;
                  try { await navigator.clipboard.writeText(t); alert('已复制：' + t); }
                  catch { prompt('请复制：', t); }
                }
              </script>
            </body>
            </html>
            """;
    }

    private static string TruncatePreview(string text)
    {
        string singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return singleLine.Length > 46 ? singleLine[..46] + "…" : singleLine;
    }

    public async ValueTask DisposeAsync()
    {
        if (_mdns != null)
        {
            await _mdns.DisposeAsync();
            _mdns = null;
        }
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
        if (_pushService is IDisposable disposablePush)
        {
            disposablePush.Dispose();
        }
    }
}
