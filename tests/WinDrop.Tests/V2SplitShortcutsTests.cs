using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using WinDrop.Core.Models;
using WinDrop.Core.Network;
using WinDrop.Core.Server;
using WinDrop.Core.Services;
using Xunit;

namespace WinDrop.Tests;

public sealed class V2SplitShortcutsTests : IAsyncLifetime
{
    private string _tempRoot = string.Empty;
    private WinDropStateService _state = null!;
    private InMemoryClipboardProvider _clipboard = null!;
    private WinDropServer _server = null!;
    private HttpClient _http = null!;

    private const string TestKey = "v2testkey";

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "WinDropV2Tests_" + Guid.NewGuid().ToString("N"));
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
        int port = NetworkHelper.FindAvailablePort(58700);
        _server = new WinDropServer(_state, clipboard: _clipboard, enableMdns: false);
        await _server.StartAsync(overridePort: port, bindLoopbackOnly: true);

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
    public async Task V2Send_SupportsMultipartMultipleFiles_PlainText_AndRawBinaryStream()
    {
        // 1. 无密钥返回 401
        var unauthRes = await _http.PostAsync("/v2/send", new StringContent("hi", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthRes.StatusCode);

        // 2. Multipart 多文件上传，校验 SHA-256 完整性
        byte[] imgBytes = RandomNumberGenerator.GetBytes(128 * 1024);
        byte[] pdfBytes = RandomNumberGenerator.GetBytes(64 * 1024);

        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent(imgBytes), "file", "旅行照片.JPG");
            form.Add(new ByteArrayContent(pdfBytes), "file", "合同文档.pdf");

            using var req = new HttpRequestMessage(HttpMethod.Post, "/v2/send");
            req.Headers.TryAddWithoutValidation("Authorization", TestKey);
            req.Content = form;

            var res = await _http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            string bodyText = await res.Content.ReadAsStringAsync();
            Assert.Contains("已保存 2 个文件", bodyText);
        }

        Assert.Equal(SHA256.HashData(imgBytes), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(_state.Config.SaveDirectory, "旅行照片.JPG"))));
        Assert.Equal(SHA256.HashData(pdfBytes), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(_state.Config.SaveDirectory, "合同文档.pdf"))));

        // 3. text/plain 纯文本直接写入电脑剪贴板
        using (var txtReq = new HttpRequestMessage(HttpMethod.Post, "/v2/send"))
        {
            txtReq.Headers.TryAddWithoutValidation("Authorization", TestKey);
            txtReq.Content = new StringContent("https://example.com/v2-share", Encoding.UTF8, "text/plain");
            var txtRes = await _http.SendAsync(txtReq);
            Assert.Equal(HttpStatusCode.OK, txtRes.StatusCode);
        }
        var snap = await _clipboard.GetClipboardSnapshotAsync();
        Assert.Equal("https://example.com/v2-share", snap.Text);

        // 4. 原始二进制流（无 X-File-Name 时根据 Content-Type 自动推断 .png 扩展名）
        byte[] rawPng = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3 };
        using (var binReq = new HttpRequestMessage(HttpMethod.Post, "/v2/send?format=json"))
        {
            binReq.Headers.TryAddWithoutValidation("Authorization", TestKey);
            binReq.Content = new ByteArrayContent(rawPng);
            binReq.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            var binRes = await _http.SendAsync(binReq);
            Assert.Equal(HttpStatusCode.OK, binRes.StatusCode);

            using var doc = JsonDocument.Parse(await binRes.Content.ReadAsStringAsync());
            string savedName = doc.RootElement.GetProperty("data").GetProperty("files")[0].GetString()!;
            Assert.EndsWith(".png", savedName, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(rawPng, await File.ReadAllBytesAsync(Path.Combine(_state.Config.SaveDirectory, savedName)));
        }
    }

    [Fact]
    public async Task V2Receive_SmartlyClassifiesImagesVideosMixedFilesClipboardScreenshotUrlsAndText()
    {
        string workDir = Path.Combine(_tempRoot, "WorkFiles");
        Directory.CreateDirectory(workDir);
        string img1 = Path.Combine(workDir, "p1.heic");
        string img2 = Path.Combine(workDir, "p2.jpg");
        string vid1 = Path.Combine(workDir, "clip.mov");
        string pdf1 = Path.Combine(workDir, "doc.pdf");

        byte[] vidBytes = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 500).Select(i => $"{i:D4}")));
        await File.WriteAllBytesAsync(img1, new byte[] { 1, 2, 3 });
        await File.WriteAllBytesAsync(img2, new byte[] { 4, 5, 6 });
        await File.WriteAllBytesAsync(vid1, vidBytes);
        await File.WriteAllBytesAsync(pdf1, new byte[] { 7, 8, 9 });

        // 1. 纯图片列表 -> category = "image", action = "media"
        await _clipboard.SetFilesAsync(new[] { img1, img2 });
        var rImg = await GetV2ReceiveJsonAsync();
        Assert.Equal(V2ContentCategory.Image, rImg.Category);
        Assert.Equal(V2ReceiveAction.Media, rImg.Action);
        Assert.Equal(2, rImg.Files.Count);
        Assert.Equal(2, rImg.Urls.Count);

        // 2. 纯视频列表 -> category = "video", action = "media"，支持 ?direct=1 与 Range 206 断点续传
        await _clipboard.SetFilesAsync(new[] { vid1 });
        var rVid = await GetV2ReceiveJsonAsync();
        Assert.Equal(V2ContentCategory.Video, rVid.Category);
        Assert.Equal(V2ReceiveAction.Media, rVid.Action);
        string ticketUrl = rVid.Urls[0];
        Assert.Equal(rVid.Files[0].Url, ticketUrl);

        using (var rangeReq = new HttpRequestMessage(HttpMethod.Get, new Uri(ticketUrl).PathAndQuery))
        {
            rangeReq.Headers.Range = new RangeHeaderValue(10, 29);
            var rangeRes = await _http.SendAsync(rangeReq);
            Assert.Equal(HttpStatusCode.PartialContent, rangeRes.StatusCode);
            Assert.Equal(vidBytes[10..30], await rangeRes.Content.ReadAsByteArrayAsync());
        }

        // direct=1 返回二进制流与分类头
        using (var dirReq = new HttpRequestMessage(HttpMethod.Get, "/v2/receive?direct=1"))
        {
            dirReq.Headers.TryAddWithoutValidation("Authorization", TestKey);
            var dirRes = await _http.SendAsync(dirReq);
            Assert.Equal(HttpStatusCode.OK, dirRes.StatusCode);
            Assert.Equal(V2ContentCategory.Video, dirRes.Headers.GetValues("X-WinDrop-Category").First());
            Assert.Equal(vidBytes, await dirRes.Content.ReadAsByteArrayAsync());
        }

        // 2.5 图片+视频混合 -> action = "media"，可一次性全存入相册
        await _clipboard.SetFilesAsync(new[] { img1, vid1 });
        var rMixedMedia = await GetV2ReceiveJsonAsync();
        Assert.Equal(V2ContentCategory.File, rMixedMedia.Category);
        Assert.Equal(V2ReceiveAction.Media, rMixedMedia.Action);
        Assert.Equal(2, rMixedMedia.Urls.Count);

        // 3. 混合文件（图片 + PDF）-> action = "file", category = "file"，各子项保留精确 category
        await _clipboard.SetFilesAsync(new[] { img1, pdf1 });
        var rMix = await GetV2ReceiveJsonAsync();
        Assert.Equal(V2ContentCategory.File, rMix.Category);
        Assert.Equal(V2ReceiveAction.File, rMix.Action);
        Assert.Equal(V2ContentCategory.Image, rMix.Files[0].Category);
        Assert.Equal(V2ContentCategory.File, rMix.Files[1].Category);
        Assert.Equal(2, rMix.Urls.Count);

        // 4. 剪贴板 PNG 截图 -> action = "media"，自动落盘并下发短 Ticket 下载链接
        byte[] shotPng = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 99, 88, 77 };
        await _clipboard.SetImagePngAsync(shotPng);
        var rShot = await GetV2ReceiveJsonAsync();
        Assert.Equal(V2ContentCategory.Image, rShot.Category);
        Assert.Equal(V2ReceiveAction.Media, rShot.Action);
        Assert.Single(rShot.Files);
        Assert.Single(rShot.Urls);
        byte[] downloadedPng = await _http.GetByteArrayAsync(new Uri(rShot.Urls[0]).PathAndQuery);
        Assert.Equal(shotPng, downloadedPng);

        // 5. 伪造短 Ticket 必须返回 404
        var badTicketRes = await _http.GetAsync("/v2/dl/invalid_ticket_000");
        Assert.Equal(HttpStatusCode.NotFound, badTicketRes.StatusCode);

        // 6. URL 与普通文本分类 -> urls 必须为空数组，防止单循环执行
        await _clipboard.SetTextAsync("https://github.com");
        var rUrl = await GetV2ReceiveJsonAsync();
        Assert.Equal(V2ContentCategory.Url, rUrl.Category);
        Assert.Equal(V2ReceiveAction.Url, rUrl.Action);
        Assert.Equal("https://github.com", rUrl.Text);
        Assert.Empty(rUrl.Urls);

        await _clipboard.SetTextAsync("普通剪贴板文字");
        var rTxt = await GetV2ReceiveJsonAsync();
        Assert.Equal(V2ContentCategory.Text, rTxt.Category);
        Assert.Equal(V2ReceiveAction.Text, rTxt.Action);
        Assert.Equal("普通剪贴板文字", rTxt.Text);
        Assert.Empty(rTxt.Urls);
    }

    [Fact]
    public void ShortcutBuilder_GeneratesValidXmlPlistWithCompleteV2WorkflowActions()
    {
        byte[] sendBytes = ShortcutBuilderService.BuildSendToWindowsShortcutPlist("MY-PC.local", 58446, "k12345");
        byte[] recvBytes = ShortcutBuilderService.BuildReceiveFromWindowsShortcutPlist("MY-PC.local", 58446, "k12345");

        string sendXml = Encoding.UTF8.GetString(sendBytes);
        string recvXml = Encoding.UTF8.GetString(recvBytes);

        // 1. 严格 XML 结构校验（排除转义或标签闭合错误）
        var sendDoc = XDocument.Parse(sendXml);
        var recvDoc = XDocument.Parse(recvXml);
        Assert.Equal("plist", sendDoc.Root?.Name.LocalName);
        Assert.Equal("plist", recvDoc.Root?.Name.LocalName);

        // 2. 验证关键动作标识符与接口地址均已准确生成
        Assert.Contains("http://MY-PC.local:58446/v2/send", sendXml);
        Assert.Contains("<integer>5</integer>", sendXml);
        Assert.Contains("WFTokenAttachmentParameterState", sendXml);
        Assert.Contains("ShowHeaders", sendXml);
        Assert.Contains("is.workflow.actions.count", sendXml);
        Assert.Contains("is.workflow.actions.repeat.each", sendXml);
        Assert.Contains("is.workflow.actions.notification", sendXml);

        Assert.Contains("http://MY-PC.local:58446/v2/receive", recvXml);
        Assert.Contains("<string>action</string>", recvXml);
        Assert.Contains("<string>urls</string>", recvXml);
        Assert.Contains("<string>summary</string>", recvXml);
        Assert.Contains("is.workflow.actions.conditional", recvXml);
        Assert.Contains("is.workflow.actions.openurl", recvXml);
        Assert.Contains("is.workflow.actions.setclipboard", recvXml);
        Assert.Contains("is.workflow.actions.savetocameraroll", recvXml);
        Assert.Contains("is.workflow.actions.documentpicker.save", recvXml);

        // 3. 验证 macOS 官方签名脚本内容
        string macScript = ShortcutBuilderService.BuildMacSignScript();
        Assert.Contains("shortcuts sign --mode anyone", macScript);
    }

    private async Task<V2ReceiveResponse> GetV2ReceiveJsonAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/v2/receive");
        req.Headers.TryAddWithoutValidation("Authorization", TestKey);
        var res = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var body = JsonSerializer.Deserialize<V2ReceiveResponse>(await res.Content.ReadAsStringAsync(), options);
        Assert.NotNull(body);
        return body!;
    }
}
