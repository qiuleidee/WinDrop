using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WinDrop.Core.Network;

/// <summary>
/// 网卡与可用局域网 IPv4 端点描述
/// </summary>
public sealed record LocalNetworkEndpoint(
    string InterfaceName,
    string Description,
    IPAddress IpAddress,
    int Priority,
    IPAddress? SubnetMask = null,
    int InterfaceIndex = 0,
    bool IsVirtualOrTunnel = false,
    string CategoryLabel = "局域网");

/// <summary>
/// 复杂网络环境（虚拟网卡 / 代理 TUN / 系统代理 / 防火墙）诊断报告
/// </summary>
public sealed record NetworkDiagnosticsReport(
    IPAddress PrimaryIp,
    IReadOnlyList<LocalNetworkEndpoint> CandidateEndpoints,
    IReadOnlyList<LocalNetworkEndpoint> IgnoredVirtualEndpoints,
    bool HasSystemProxy,
    string? SystemProxyAddress,
    bool FirewallConfigured,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Suggestions);

/// <summary>
/// 多网卡局域网 IP 智能探测、TUN 过滤、子网匹配与可用端口分配器
/// </summary>
public static class NetworkHelper
{
    private static readonly string[] VirtualOrTunnelKeywords =
    {
        // 代理客户端 / TUN 虚拟网卡特征
        "Wintun", "sing-tun", "SingBox", "sing-box", "Clash", "Mihomo", "Meta", "FlClash",
        "v2ray", "Xray", "NekoBox", "Hiddify", "Karing", "Nekoray", "Surge", "Quantumult",
        // VPN / 异地组网虚拟网卡特征
        "WireGuard", "Tailscale", "ZeroTier", "Radmin", "Hamachi", "TAP-Windows", "TAP-Win",
        "OpenVPN", "AnyConnect", "Sangfor", "EasyConnect", "GlobalProtect", "NordLynx",
        "ProtonVPN", "CloudflareWARP", "WARP", "Mullvad", "SoftEther", "L2TP", "PPTP",
        // 容器与本地虚拟机网桥
        "Hyper-V", "WSL", "Docker", "VMware", "VirtualBox", "vEthernet", "Npcap", "Loopback"
    };

    public static List<LocalNetworkEndpoint> GetLocalIPv4Endpoints()
    {
        var results = new List<LocalNetworkEndpoint>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            string desc = nic.Description ?? string.Empty;
            string name = nic.Name ?? string.Empty;

            IPInterfaceProperties ipProps;
            try
            {
                ipProps = nic.GetIPProperties();
            }
            catch
            {
                continue;
            }

            int ifIndex = 0;
            try
            {
                ifIndex = ipProps.GetIPv4Properties()?.Index ?? 0;
            }
            catch { }

            var unicastIps = ipProps.UnicastAddresses
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .ToList();

            var localIpStrings = unicastIps
                .Select(u => u.Address.ToString())
                .ToHashSet(StringComparer.Ordinal);

            // 过滤空网关及 TUN 网卡常用的“把自己设成默认网关”的虚假网关
            bool hasValidGateway = ipProps.GatewayAddresses.Any(g =>
            {
                if (g.Address.AddressFamily != AddressFamily.InterNetwork)
                    return false;
                string gs = g.Address.ToString();
                return gs != "0.0.0.0" && !localIpStrings.Contains(gs);
            });

            foreach (var unicast in unicastIps)
            {
                byte[] bytes = unicast.Address.GetAddressBytes();
                // 排除 127.x.x.x 与 169.254.x.x 链路本地无效地址
                if (bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254))
                    continue;

                var endpoint = ClassifyAdapterAndScore(
                    name,
                    desc,
                    nic.NetworkInterfaceType,
                    unicast.Address,
                    hasValidGateway,
                    unicast.IPv4Mask,
                    ifIndex);

                results.Add(endpoint);
            }
        }

        return results
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.IpAddress.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 综合网卡类型、驱动描述、IP 段、是否移动热点与有效网关计算联机优先级（纯函数，便于测试）
    /// </summary>
    public static LocalNetworkEndpoint ClassifyAdapterAndScore(
        string interfaceName,
        string description,
        NetworkInterfaceType nicType,
        IPAddress ipAddress,
        bool hasValidGateway,
        IPAddress? subnetMask = null,
        int interfaceIndex = 0)
    {
        string name = interfaceName ?? string.Empty;
        string desc = description ?? string.Empty;
        byte[] bytes = ipAddress.GetAddressBytes();

        // 198.18.0.0/15（代理 Fake-IP / 基准测试保留网段）
        bool isFakeIpRange = bytes.Length == 4 && bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19);
        // 100.64.0.0/10（CGNAT / Tailscale 等常见虚拟组网网段）
        bool isCgnatRange = bytes.Length == 4 && bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;

        // Windows 原生移动热点（Microsoft Wi-Fi Direct Virtual Adapter / 192.168.137.x）
        bool isHotspotIp = bytes.Length == 4 && bytes[0] == 192 && bytes[1] == 168 && bytes[2] == 137;
        bool isWiFiDirectHotspot =
            isHotspotIp ||
            desc.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("本地连接*", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Local Area Connection*", StringComparison.OrdinalIgnoreCase);

        bool matchesVirtualKeyword = VirtualOrTunnelKeywords.Any(k =>
            desc.Contains(k, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(k, StringComparison.OrdinalIgnoreCase));

        bool isVirtualOrTunnel =
            !isWiFiDirectHotspot &&
            (nicType == NetworkInterfaceType.Tunnel ||
             nicType == NetworkInterfaceType.Ppp ||
             isFakeIpRange ||
             isCgnatRange ||
             matchesVirtualKeyword);

        if (isVirtualOrTunnel)
        {
            int virtualScore = isFakeIpRange ? 1 : (nicType == NetworkInterfaceType.Tunnel || isCgnatRange ? 3 : 5);
            return new LocalNetworkEndpoint(
                name,
                desc,
                ipAddress,
                virtualScore,
                subnetMask,
                interfaceIndex,
                IsVirtualOrTunnel: true,
                CategoryLabel: isFakeIpRange ? "代理 Fake-IP/TUN" : "TUN/虚拟网卡");
        }

        if (isWiFiDirectHotspot)
        {
            return new LocalNetworkEndpoint(
                name,
                desc,
                ipAddress,
                140,
                subnetMask ?? IPAddress.Parse("255.255.255.0"),
                interfaceIndex,
                IsVirtualOrTunnel: false,
                CategoryLabel: "Windows 移动热点");
        }

        bool isPrivateLan = IsPrivateRfc1918(bytes);
        int priority;
        string category;

        if (nicType == NetworkInterfaceType.Wireless80211)
        {
            priority = 110 + (hasValidGateway ? 15 : 0) + (isPrivateLan ? 2 : 0);
            category = "物理 Wi-Fi";
        }
        else if (IsPhysicalEthernetType(nicType))
        {
            priority = 100 + (hasValidGateway ? 15 : 0) + (isPrivateLan ? 2 : 0);
            category = "物理有线网卡";
        }
        else
        {
            priority = 70 + (hasValidGateway ? 10 : 0) + (isPrivateLan ? 2 : 0);
            category = "其他物理网卡";
        }

        return new LocalNetworkEndpoint(
            name,
            desc,
            ipAddress,
            priority,
            subnetMask,
            interfaceIndex,
            IsVirtualOrTunnel: false,
            CategoryLabel: category);
    }

    /// <summary>
    /// 返回所有优先用于手机直连的真实局域网网卡（自动剔除 TUN、Fake-IP 与容器虚拟网卡；若全为虚拟网卡则安全兜底）
    /// </summary>
    public static List<LocalNetworkEndpoint> GetEligibleLanEndpoints(string? preferredIp = null)
    {
        var all = GetLocalIPv4Endpoints();
        var eligible = all.Where(e => !e.IsVirtualOrTunnel).ToList();

        if (!string.IsNullOrWhiteSpace(preferredIp))
        {
            var matched = all.FirstOrDefault(e => string.Equals(e.IpAddress.ToString(), preferredIp.Trim(), StringComparison.Ordinal));
            if (matched != null && !eligible.Contains(matched))
            {
                eligible.Insert(0, matched);
            }
        }

        if (eligible.Count > 0)
            return eligible;

        // 极端环境没有任何物理网卡时，至少排除 198.18 Fake-IP 后兜底返回
        var fallback = all.Where(e =>
        {
            byte[] b = e.IpAddress.GetAddressBytes();
            return !(b.Length == 4 && b[0] == 198 && (b[1] == 18 || b[1] == 19));
        }).ToList();

        return fallback.Count > 0 ? fallback : all;
    }

    /// <summary>
    /// 获取最适合手机连接的本机局域网主 IPv4 地址（支持用户手动锁定首选 IP）
    /// </summary>
    public static IPAddress GetPrimaryLocalIPv4(string? preferredIp = null)
    {
        var all = GetLocalIPv4Endpoints();
        if (!string.IsNullOrWhiteSpace(preferredIp))
        {
            var locked = all.FirstOrDefault(e => string.Equals(e.IpAddress.ToString(), preferredIp.Trim(), StringComparison.Ordinal));
            if (locked != null)
                return locked.IpAddress;
        }

        var eligible = GetEligibleLanEndpoints(preferredIp);
        if (eligible.Count > 0)
        {
            return eligible[0].IpAddress;
        }

        return IPAddress.Loopback;
    }

    /// <summary>
    /// 为发起 mDNS 查询的对端手机 IP 选择同子网物理网卡 IP，避免多网卡/开热点时回传不可达网卡 IP
    /// </summary>
    public static IPAddress SelectBestIpForRemotePeer(
        IPAddress? remoteIp,
        IReadOnlyList<LocalNetworkEndpoint> endpoints,
        string? preferredIp = null)
    {
        if (endpoints.Count == 0)
            return IPAddress.Loopback;

        var nonVirtual = endpoints.Where(e => !e.IsVirtualOrTunnel).ToList();
        var pool = nonVirtual.Count > 0 ? nonVirtual : endpoints.ToList();

        if (remoteIp != null && remoteIp.AddressFamily == AddressFamily.InterNetwork)
        {
            var sameSubnet = pool
                .Where(e => IsSameSubnet(remoteIp, e.IpAddress, e.SubnetMask))
                .OrderByDescending(e => string.Equals(e.IpAddress.ToString(), preferredIp?.Trim(), StringComparison.Ordinal))
                .ThenByDescending(e => e.Priority)
                .FirstOrDefault();

            if (sameSubnet != null)
                return sameSubnet.IpAddress;
        }

        if (!string.IsNullOrWhiteSpace(preferredIp))
        {
            var locked = endpoints.FirstOrDefault(e => string.Equals(e.IpAddress.ToString(), preferredIp.Trim(), StringComparison.Ordinal));
            if (locked != null)
                return locked.IpAddress;
        }

        return pool.OrderByDescending(e => e.Priority).First().IpAddress;
    }

    public static bool IsSameSubnet(IPAddress a, IPAddress b, IPAddress? subnetMask = null)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork || b.AddressFamily != AddressFamily.InterNetwork)
            return false;

        byte[] ba = a.GetAddressBytes();
        byte[] bb = b.GetAddressBytes();
        byte[] mask = (subnetMask != null && subnetMask.AddressFamily == AddressFamily.InterNetwork)
            ? subnetMask.GetAddressBytes()
            : new byte[] { 255, 255, 255, 0 };

        for (int i = 0; i < 4; i++)
        {
            if ((ba[i] & mask[i]) != (bb[i] & mask[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 判断主机名或 IP 是否属于局域网/本机回环（推送等请求可据此直接绕过系统代理）
    /// </summary>
    public static bool IsPrivateOrLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        string clean = host.Trim().Trim('[', ']');
        if (clean.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            clean.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(clean, out var ip))
            return false;

        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            return IsPrivateRfc1918(b) || (b[0] == 169 && b[1] == 254);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] b = ip.GetAddressBytes();
            // ULA fc00::/7 或 Link-Local fe80::/10
            return ip.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    /// <summary>
    /// 检测当前进程是否命中系统全局代理并返回代理地址
    /// </summary>
    public static (bool HasProxy, string? ProxyAddress) TryGetSystemProxyInfo()
    {
        try
        {
            var probeUri = new Uri("https://api.day.app/push");
            IWebProxy proxy = HttpClient.DefaultProxy;
            if (proxy == null || proxy.IsBypassed(probeUri))
                return (false, null);

            Uri? proxyUri = proxy.GetProxy(probeUri);
            if (proxyUri == null || string.Equals(proxyUri.Host, probeUri.Host, StringComparison.OrdinalIgnoreCase))
                return (false, null);

            return (true, proxyUri.ToString().TrimEnd('/'));
        }
        catch
        {
            return (false, null);
        }
    }

    /// <summary>
    /// 生成复杂网络环境（虚拟网卡 / 代理 TUN / 系统代理 / 防火墙）中文自检诊断报告
    /// </summary>
    public static NetworkDiagnosticsReport DiagnoseEnvironment(int port, bool firewallOk, string? preferredIp = null)
    {
        var all = GetLocalIPv4Endpoints();
        var eligible = GetEligibleLanEndpoints(preferredIp);
        var virtuals = all.Where(e => e.IsVirtualOrTunnel).ToList();
        var primary = GetPrimaryLocalIPv4(preferredIp);
        var (hasProxy, proxyAddr) = TryGetSystemProxyInfo();

        var warnings = new List<string>();
        var suggestions = new List<string>();

        if (eligible.Count == 0 || Equals(primary, IPAddress.Loopback))
        {
            warnings.Add("未检测到有效物理 Wi-Fi、有线网或移动热点局域网 IP。");
            suggestions.Add("请确认电脑已连接路由器 Wi-Fi/网线，或开启「Windows 移动热点」让 iPhone 直连。");
        }

        if (virtuals.Count > 0)
        {
            string vNames = string.Join("、", virtuals.Select(v => $"{v.CategoryLabel}({v.IpAddress})").Distinct().Take(3));
            warnings.Add($"检测到 {virtuals.Count} 个 TUN/虚拟网卡：{vNames}。");
            suggestions.Add("WinDrop 已自动屏蔽 TUN/虚拟网卡并分网卡绑定 mDNS；若手机仍找不到 .local，请在手机快捷指令中直接改用下方推荐的局域网 IP，或在电脑端设置中锁定正确网卡。");
        }

        if (hasProxy)
        {
            warnings.Add($"检测到系统代理处于开启状态：{proxyAddr}。");
            suggestions.Add("请确保代理客户端已勾选「绕过局域网地址」；若使用手机端代理，请将电脑 IP 与 .local 设为 DIRECT（或暂时关闭代理测试）。");
        }

        if (!firewallOk)
        {
            warnings.Add($"Windows 防火墙尚未放行端口 TCP {port}（或处于公用网络配置被系统阻止）。");
            suggestions.Add("请点击设置窗中的「🛡️ 一键放行防火墙」，程序会自动清除公用网络残留阻止规则并放行当前程序与端口。");
        }

        if (warnings.Count == 0)
        {
            suggestions.Add($"当前网络环境健康，推荐连接地址：http://{primary}:{port}/setup");
            suggestions.Add("若身处酒店/校园网/公司公共 Wi-Fi 且仍联机失败，多为路由器开启了「AP 客户端隔离」，开启 Windows 自带移动热点（192.168.137.1）让手机直连即可彻底解决。");
        }

        return new NetworkDiagnosticsReport(
            primary,
            eligible,
            virtuals,
            hasProxy,
            proxyAddr,
            firewallOk,
            warnings,
            suggestions);
    }

    private static bool IsPrivateRfc1918(byte[] b)
    {
        if (b.Length != 4)
            return false;
        return b[0] == 10 ||
               (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
               (b[0] == 192 && b[1] == 168);
    }

    private static bool IsPhysicalEthernetType(NetworkInterfaceType type) =>
        type is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.FastEthernetT;

    /// <summary>
    /// 检测首选端口是否可用，若被占用则自动递增寻找空闲端口（PRD 第 4 章规格）
    /// </summary>
    public static int FindAvailablePort(int preferredPort = 53318, int maxRetries = 30)
    {
        for (int port = preferredPort; port < preferredPort + maxRetries; port++)
        {
            if (IsPortAvailable(port))
            {
                return port;
            }
        }

        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        int assigned = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return assigned;
    }

    public static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
