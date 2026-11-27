using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using WinDrop.Core.Models;
using WinDrop.Core.Network;
using WinDrop.Core.Server;
using WinDrop.Core.Services;
using Xunit;

namespace WinDrop.Tests;

public sealed class ComplexNetworkResilienceTests
{
    private sealed class FailingProxyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("模拟失效的系统代理连接失败");
        }
    }

    private sealed class RecordingSuccessHandler : HttpMessageHandler
    {
        public List<Uri?> RequestedUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":200,\"message\":\"success\"}")
            });
        }
    }

    [Fact]
    public void NetworkHelper_FiltersTunFakeIpAndVirtualAdapters_WhilePrioritizingHotspotAndRealLan()
    {
        // 1. 代理软件 TUN Fake-IP (198.18.0.1) 与 WSL/Clash 虚拟网卡必须大幅降权且标记为 IsVirtualOrTunnel
        var clashTun = NetworkHelper.ClassifyAdapterAndScore(
            "Mihomo",
            "Wintun Userspace Tunnel",
            NetworkInterfaceType.Ethernet,
            IPAddress.Parse("198.18.0.1"),
            hasValidGateway: true);
        Assert.True(clashTun.IsVirtualOrTunnel);
        Assert.True(clashTun.Priority <= 10);

        var singTun = NetworkHelper.ClassifyAdapterAndScore(
            "sing-tun",
            "sing-box Tunnel Adapter",
            NetworkInterfaceType.Ethernet,
            IPAddress.Parse("172.19.0.1"),
            hasValidGateway: true);
        Assert.True(singTun.IsVirtualOrTunnel);
        Assert.True(singTun.Priority <= 10);

        var tailscale = NetworkHelper.ClassifyAdapterAndScore(
            "Tailscale",
            "Tailscale Tunnel",
            NetworkInterfaceType.Ethernet,
            IPAddress.Parse("100.88.12.34"),
            hasValidGateway: false);
        Assert.True(tailscale.IsVirtualOrTunnel);
        Assert.True(tailscale.Priority <= 10);

        // 2. 带真实网关的物理 Wi-Fi 与物理有线应高优保留
        var wifi = NetworkHelper.ClassifyAdapterAndScore(
            "WLAN",
            "Realtek 8852BE Wireless LAN WiFi 6 PCI-E NIC",
            NetworkInterfaceType.Wireless80211,
            IPAddress.Parse("192.168.3.25"),
            hasValidGateway: true,
            IPAddress.Parse("255.255.255.0"));
        Assert.False(wifi.IsVirtualOrTunnel);
        Assert.True(wifi.Priority >= 120);

        // 3. Windows 移动热点 (192.168.137.1) 即使底层带 Virtual 描述也必须豁免并置顶（便于突破公共 Wi-Fi AP 隔离）
        var hotspot = NetworkHelper.ClassifyAdapterAndScore(
            "本地连接* 11",
            "Microsoft Wi-Fi Direct Virtual Adapter #2",
            NetworkInterfaceType.Wireless80211,
            IPAddress.Parse("192.168.137.1"),
            hasValidGateway: false,
            IPAddress.Parse("255.255.255.0"));
        Assert.False(hotspot.IsVirtualOrTunnel);
        Assert.True(hotspot.Priority > wifi.Priority);
    }

    [Fact]
    public void NetworkHelper_SelectBestIpForRemotePeer_MatchesRequestingSubnetEvenWithMultipleNics()
    {
        var wifi = NetworkHelper.ClassifyAdapterAndScore(
            "WLAN",
            "Intel Wireless",
            NetworkInterfaceType.Wireless80211,
            IPAddress.Parse("192.168.50.10"),
            hasValidGateway: true,
            IPAddress.Parse("255.255.255.0"));

        var hotspot = NetworkHelper.ClassifyAdapterAndScore(
            "本地连接* 9",
            "Microsoft Wi-Fi Direct Virtual Adapter",
            NetworkInterfaceType.Wireless80211,
            IPAddress.Parse("192.168.137.1"),
            hasValidGateway: false,
            IPAddress.Parse("255.255.255.0"));

        var tun = NetworkHelper.ClassifyAdapterAndScore(
            "Clash",
            "Wintun",
            NetworkInterfaceType.Ethernet,
            IPAddress.Parse("198.18.0.1"),
            hasValidGateway: true);

        var endpoints = new[] { tun, hotspot, wifi };

        // 来自 192.168.50.x 路由器 Wi-Fi 的手机查 mDNS -> 必须匹配回复 192.168.50.10（而不是热点或 TUN）
        IPAddress replyForWifiPhone = NetworkHelper.SelectBestIpForRemotePeer(IPAddress.Parse("192.168.50.88"), endpoints);
        Assert.Equal(IPAddress.Parse("192.168.50.10"), replyForWifiPhone);

        // 连入电脑移动热点的手机 192.168.137.x 查 mDNS -> 必须匹配回复 192.168.137.1
        IPAddress replyForHotspotPhone = NetworkHelper.SelectBestIpForRemotePeer(IPAddress.Parse("192.168.137.42"), endpoints);
        Assert.Equal(IPAddress.Parse("192.168.137.1"), replyForHotspotPhone);
    }

    [Fact]
    public void NetworkHelper_RecognizesPrivateAndLocalHostsForProxyBypass()
    {
        Assert.True(NetworkHelper.IsPrivateOrLocalHost("localhost"));
        Assert.True(NetworkHelper.IsPrivateOrLocalHost("MY-PC.local"));
        Assert.True(NetworkHelper.IsPrivateOrLocalHost("192.168.137.1"));
        Assert.True(NetworkHelper.IsPrivateOrLocalHost("10.0.0.5"));
        Assert.True(NetworkHelper.IsPrivateOrLocalHost("172.16.10.2"));
        Assert.False(NetworkHelper.IsPrivateOrLocalHost("api.day.app"));
        Assert.False(NetworkHelper.IsPrivateOrLocalHost("8.8.8.8"));
    }

    [Fact]
    public async Task BarkPushService_AutomaticallyFallsBackToDirectConnection_WhenSystemProxyFails()
    {
        using var failingProxyClient = new HttpClient(new FailingProxyHandler());
        var directHandler = new RecordingSuccessHandler();
        using var directClient = new HttpClient(directHandler);
        using var pushService = new BarkPushService(failingProxyClient, directClient);

        var config = new AppConfig
        {
            EnablePushNotification = true,
            BarkKeyOrUrl = "https://api.day.app/TestKey999",
            ReceiveShortcutName = "WinDrop接收"
        };

        var (ok, msg) = await pushService.SendShortcutWakePushAsync(config, "标题", "内容");
        Assert.True(ok);
        Assert.Contains("绕过系统代理直连", msg);
        Assert.Single(directHandler.RequestedUris);
        Assert.Equal("https://api.day.app/push", directHandler.RequestedUris[0]?.ToString());
    }

    [Fact]
    public async Task Server_PingEndpoint_IsAccessibleWithoutAuth_AndReturnsNetworkMetadata()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "WinDropPingTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var state = new WinDropStateService(tempRoot);
            state.UpdateConfig(c => c.Key = "secret_ping_test");

            int port = NetworkHelper.FindAvailablePort(58750);
            await using var server = new WinDropServer(state, enableMdns: false);
            await server.StartAsync(overridePort: port, bindLoopbackOnly: true);

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.ListeningPort}") };
            var res = await http.GetAsync("/ping");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(server.ListeningPort, doc.RootElement.GetProperty("port").GetInt32());
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
