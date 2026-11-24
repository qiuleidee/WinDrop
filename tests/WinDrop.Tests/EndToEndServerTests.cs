using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinDrop.Core.Network;
using WinDrop.Core.Server;
using WinDrop.Core.Services;
using Xunit;

namespace WinDrop.Tests;

public sealed class EndToEndServerTests : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private WinDropStateService _state = null!;
    private InMemoryClipboardProvider _clipboard = null!;
    private WinDropServer _server = null!;
    private HttpClient _http = null!;

    private const string TestKey = "wd8899";

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "WinDropE2E_" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(_tempRoot, "AppData");
        string saveDir = Path.Combine(_tempRoot, "Downloads");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(saveDir);

        _state = new WinDropStateService(dataDir);
        _state.UpdateConfig(c =>
        {
            c.Key = TestKey;
            c.SaveDirectory = saveDir;
        });

        _clipboard = new InMemoryClipboardProvider();
        int freePort = NetworkHelper.FindAvailablePort(58500);
        _server = new WinDropServer(_state, clipboard: _clipboard, enableMdns: false);
        await _server.StartAsync(overridePort: freePort, bindLoopbackOnly: true);

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.ListeningPort}") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task iOSShareSheet_AutoStreamsFileToWindows_WithoutBlockingPrompt()
    {
        // 1. 连通性测试接口免鉴权返回 Hello World!
        var rootRes = await _http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, rootRes.StatusCode);
        Assert.Equal("Hello World!", await rootRes.Content.ReadAsStringAsync());

        // 2. 错误密钥请求被 401 拦截
        using (var badReq = new HttpRequestMessage(HttpMethod.Get, "/clipboard"))
        {
            badReq.Headers.TryAddWithoutValidation("Authorization", "wrong_key");
            var badRes = await _http.SendAsync(badReq);
            Assert.Equal(HttpStatusCode.Unauthorized, badRes.StatusCode);
        }

        // 3. 模拟 iPhone 共享表单自动发送 3MB 视频到 POST /file
        byte[] videoBytes = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        byte[] expectedHash = SHA256.HashData(videoBytes);

        using (var form = new MultipartFormDataContent())
        {
            var content = new ByteArrayContent(videoBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("video/quicktime");
            form.Add(content, "file", "../../iPhone_Share_Video.MOV");

            using var req = new HttpRequestMessage(HttpMethod.Post, "/file");
            req.Headers.TryAddWithoutValidation("Authorization", TestKey);
            req.Headers.TryAddWithoutValidation("ShortcutVersion", "1.5.3");
            req.Content = form;

            var res = await _http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        }

        string savedPath = Path.Combine(_state.Config.SaveDirectory, "iPhone_Share_Video.MOV");
        Assert.True(File.Exists(savedPath));
        Assert.Equal(expectedHash, SHA256.HashData(await File.ReadAllBytesAsync(savedPath)));
    }

    [Fact]
    public async Task WindowsRightClickShareAndCopyFiles_PulledByiOSShortcut()
    {
        // 创建带中文子目录（确保标准 Base64 可能含 '/'）的两个待分享文件
        string cnSubDir = Path.Combine(_tempRoot, "右键分享测试目录");
        Directory.CreateDirectory(cnSubDir);
        string f1 = Path.Combine(cnSubDir, "照片1.jpg");
        string f2 = Path.Combine(cnSubDir, "文档2.pdf");
        byte[] f1Bytes = RandomNumberGenerator.GetBytes(64 * 1024);
        byte[] f2Bytes = RandomNumberGenerator.GetBytes(32 * 1024);
        await File.WriteAllBytesAsync(f1, f1Bytes);
        await File.WriteAllBytesAsync(f2, f2Bytes);

        // 模拟用户在资源管理器选中这两个文件点击右键「通过 WinDrop 分享」
        int sharedCount = await _server.ShareFilesFromContextMenuAsync(new[] { f1, f2 });
        Assert.Equal(2, sharedCount);

        // iPhone 运行快捷指令点「接收」 -> GET /clipboard
        List<string> encodedPaths;
        using (var req = new HttpRequestMessage(HttpMethod.Get, "/clipboard"))
        {
            req.Headers.TryAddWithoutValidation("Authorization", TestKey);
            var res = await _http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            var dataNode = doc.RootElement.GetProperty("data");
            Assert.Equal("file", dataNode.GetProperty("type").GetString());
            encodedPaths = dataNode.GetProperty("data").EnumerateArray().Select(e => e.GetString()!).ToList();
        }

        Assert.Equal(2, encodedPaths.Count);

        // iPhone 快捷指令循环调用 GET /file/{encodedPath} 下载文件
        using (var dlReq = new HttpRequestMessage(HttpMethod.Get, $"/file/{encodedPaths[0]}"))
        {
            dlReq.Headers.TryAddWithoutValidation("Authorization", TestKey);
            var dlRes = await _http.SendAsync(dlReq);
            Assert.Equal(HttpStatusCode.OK, dlRes.StatusCode);
            Assert.Equal(f1Bytes, await dlRes.Content.ReadAsByteArrayAsync());
        }

        // 尝试伪造读取未经分享授权的敏感文件 -> 必须返回 404 拒绝！
        string unsharedFile = Path.Combine(_tempRoot, "unshared_secret.txt");
        await File.WriteAllTextAsync(unsharedFile, "top_secret");
        string forgedB64 = QrAndPathHelper.EncodeFilePathBase64(unsharedFile);
        using (var forgeReq = new HttpRequestMessage(HttpMethod.Get, $"/file/{forgedB64}"))
        {
            forgeReq.Headers.TryAddWithoutValidation("Authorization", TestKey);
            var forgeRes = await _http.SendAsync(forgeReq);
            Assert.Equal(HttpStatusCode.NotFound, forgeRes.StatusCode);
        }
    }

    [Fact]
    public async Task BidirectionalClipboard_SupportsImageAndTextSync()
    {
        // 1. 电脑截图复制图片 -> iPhone 获取返回 type="img"
        byte[] samplePng = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        await _clipboard.SetImagePngAsync(samplePng);

        using (var imgReq = new HttpRequestMessage(HttpMethod.Get, "/clipboard"))
        {
            imgReq.Headers.TryAddWithoutValidation("Authorization", TestKey);
            var res = await _http.SendAsync(imgReq);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var dataNode = doc.RootElement.GetProperty("data");
            Assert.Equal("img", dataNode.GetProperty("type").GetString());
            Assert.Equal(Convert.ToBase64String(samplePng), dataNode.GetProperty("data").GetString());
        }

        // 2. iPhone 发送剪贴板文本（Form 格式，与官方快捷指令一致）-> 写入电脑剪贴板
        using (var postFormReq = new HttpRequestMessage(HttpMethod.Post, "/clipboard"))
        {
            postFormReq.Headers.TryAddWithoutValidation("Authorization", TestKey);
            postFormReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["clipboard"] = "来自 iPhone 快捷指令的一段话"
            });
            var postRes = await _http.SendAsync(postFormReq);
            Assert.Equal(HttpStatusCode.OK, postRes.StatusCode);
        }

        var current = await _clipboard.GetClipboardSnapshotAsync();
        Assert.Equal("来自 iPhone 快捷指令的一段话", current.Text);
    }
}
