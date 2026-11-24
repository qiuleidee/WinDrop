using System.Net;
using System.Text;
using System.Text.Json;
using WinDrop.Core.Models;
using WinDrop.Core.Network;
using WinDrop.Core.Server;
using WinDrop.Core.Services;
using Xunit;

namespace WinDrop.Tests;

public sealed class BarkPushTests
{
    private sealed class MockPushHttpHandler : HttpMessageHandler
    {
        public List<(Uri? RequestUri, string BodyJson)> CapturedRequests { get; } = new();
        public HttpStatusCode ResponseCode { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = "{\"code\":200,\"message\":\"success\"}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : string.Empty;
            CapturedRequests.Add((request.RequestUri, body));

            return new HttpResponseMessage(ResponseCode)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    [Theory]
    [InlineData("AbCdEf123456", "https://api.day.app", "AbCdEf123456")]
    [InlineData("https://api.day.app/AbCdEf123456/这里改成你自己的推送内容", "https://api.day.app", "AbCdEf123456")]
    [InlineData("http://192.168.1.50:8080/MyPrivateKey/", "http://192.168.1.50:8080", "MyPrivateKey")]
    public void TryParseBarkEndpoint_SupportsRawKeyOfficialUrlAndSelfHostedUrl(
        string rawInput,
        string expectedServer,
        string expectedKey)
    {
        bool ok = BarkPushService.TryParseBarkEndpoint(rawInput, out var endpoint);
        Assert.True(ok);
        Assert.NotNull(endpoint);
        Assert.Equal(expectedServer, endpoint!.ServerBaseUrl);
        Assert.Equal(expectedKey, endpoint.DeviceKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://api.day.app/")]
    [InlineData("https://api.day.app/push")]
    [InlineData("invalid key with spaces")]
    public void TryParseBarkEndpoint_RejectsInvalidInputs(string rawInput)
    {
        bool ok = BarkPushService.TryParseBarkEndpoint(rawInput, out var endpoint);
        Assert.False(ok);
        Assert.Null(endpoint);
    }

    [Fact]
    public async Task ShareFilesFromContextMenu_TriggersWakePushWithShortcutSchemeAndWhitelistsFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WinDropPushTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            string filePath = Path.Combine(tempDir, "汇报视频.mp4");
            await File.WriteAllTextAsync(filePath, "video-stub");

            var state = new WinDropStateService(Path.Combine(tempDir, "data"));
            state.UpdateConfig(c =>
            {
                c.EnablePushNotification = true;
                c.BarkKeyOrUrl = "https://api.day.app/MyTestBarkKey/示例";
                c.ReceiveShortcutName = "AirDrop Plus";
            });

            var mockHandler = new MockPushHttpHandler();
            using var mockHttp = new HttpClient(mockHandler);
            using var pushSvc = new BarkPushService(mockHttp);
            var clipboard = new InMemoryClipboardProvider();

            int port = NetworkHelper.FindAvailablePort(58600);
            await using var server = new WinDropServer(state, clipboard: clipboard, pushService: pushSvc, enableMdns: false);
            await server.StartAsync(overridePort: port, bindLoopbackOnly: true);

            int count = await server.ShareFilesFromContextMenuAsync(new[] { filePath });
            Assert.Equal(1, count);
            Assert.True(state.IsFileAuthorizedForDownload(filePath, out _));

            Assert.Single(mockHandler.CapturedRequests);
            var (uri, bodyJson) = mockHandler.CapturedRequests[0];
            Assert.Equal("https://api.day.app/push", uri?.ToString());

            using var doc = JsonDocument.Parse(bodyJson);
            var root = doc.RootElement;
            Assert.Equal("MyTestBarkKey", root.GetProperty("device_key").GetString());
            Assert.Equal("shortcuts://run-shortcut?name=AirDrop%20Plus", root.GetProperty("url").GetString());
            Assert.Contains("汇报视频.mp4", root.GetProperty("body").GetString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task PushCurrentClipboard_RespectsPrivacyModeAndSupportsUrlDirectOpen()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WinDropClipPush_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var state = new WinDropStateService(Path.Combine(tempDir, "data"));
            state.UpdateConfig(c =>
            {
                c.EnablePushNotification = true;
                c.BarkKeyOrUrl = "SecretDeviceKey99";
                c.ReceiveShortcutName = "收电脑文件";
                c.PushIncludeClipboardText = false;
            });

            var mockHandler = new MockPushHttpHandler();
            using var mockHttp = new HttpClient(mockHandler);
            using var pushSvc = new BarkPushService(mockHttp);
            var clipboard = new InMemoryClipboardProvider();
            await clipboard.SetTextAsync("银行卡验证码 952711");

            int port = NetworkHelper.FindAvailablePort(58650);
            await using var server = new WinDropServer(state, clipboard: clipboard, pushService: pushSvc, enableMdns: false);
            await server.StartAsync(overridePort: port, bindLoopbackOnly: true);

            // 1. 默认隐私模式下，外发推送绝不携带剪贴板原文
            var (ok1, _) = await server.PushCurrentClipboardToPhoneAsync();
            Assert.True(ok1);
            using (var doc1 = JsonDocument.Parse(mockHandler.CapturedRequests[0].BodyJson))
            {
                var r1 = doc1.RootElement;
                Assert.False(r1.TryGetProperty("copy", out _));
                Assert.DoesNotContain("952711", r1.GetProperty("body").GetString());
                Assert.Equal("shortcuts://run-shortcut?name=%E6%94%B6%E7%94%B5%E8%84%91%E6%96%87%E4%BB%B6", r1.GetProperty("url").GetString());
            }

            // 2. 开启附带文本且剪贴板为 URL 时，推送包含直开网页链接与自动复制参数
            state.UpdateConfig(c => c.PushIncludeClipboardText = true);
            await clipboard.SetTextAsync("https://github.com/qiuleidee/AirDropPlus.Net");
            var (ok2, _) = await server.PushCurrentClipboardToPhoneAsync();
            Assert.True(ok2);
            using (var doc2 = JsonDocument.Parse(mockHandler.CapturedRequests[1].BodyJson))
            {
                var r2 = doc2.RootElement;
                Assert.Equal("https://github.com/qiuleidee/AirDropPlus.Net", r2.GetProperty("url").GetString());
                Assert.Equal("https://github.com/qiuleidee/AirDropPlus.Net", r2.GetProperty("copy").GetString());
                Assert.Equal("1", r2.GetProperty("autoCopy").GetString());
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
