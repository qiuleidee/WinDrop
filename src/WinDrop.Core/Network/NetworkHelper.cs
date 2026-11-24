using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WinDrop.Core.Network;

/// <summary>
/// 网卡信息描述
/// </summary>
public sealed record LocalNetworkEndpoint(
    string InterfaceName,
    string Description,
    IPAddress IpAddress,
    int Priority);

/// <summary>
/// 多网卡局域网 IP 智能探测与可用端口分配器
/// 优先选择物理无线网卡（如 Intel Wireless-AC 9462）、Windows 移动热点（192.168.137.1）与物理有线网卡
/// </summary>
public static class NetworkHelper
{
    public static List<LocalNetworkEndpoint> GetLocalIPv4Endpoints()
    {
        var results = new List<LocalNetworkEndpoint>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                continue;

            string desc = nic.Description ?? string.Empty;
            string name = nic.Name ?? string.Empty;

            // 判断是否为虚拟容器网桥（WSL / Hyper-V / Docker / VMware / VirtualBox）
            bool isVirtualBridge =
                desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("Docker", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase);

            // Windows 原生移动热点（Microsoft Wi-Fi Direct Virtual Adapter）需要高优保留
            bool isWiFiDirectHotspot =
                desc.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("本地连接*", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Local Area Connection*", StringComparison.OrdinalIgnoreCase);

            var ipProps = nic.GetIPProperties();
            foreach (var unicast in ipProps.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                byte[] bytes = unicast.Address.GetAddressBytes();
                // 排除 127.x.x.x 和 169.254.x.x 链路本地无效地址
                if (bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254))
                    continue;

                int priority = 50;
                if (isWiFiDirectHotspot || (bytes[0] == 192 && bytes[1] == 168 && bytes[2] == 137))
                {
                    priority = 95; // 移动热点直连时优先生效
                }
                else if (!isVirtualBridge && nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    priority = 100; // 物理 Wi-Fi 网卡最高优（如 Intel Wireless-AC 9462）
                }
                else if (!isVirtualBridge && nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                {
                    priority = 90; // 物理有线网卡次高优
                }
                else if (isVirtualBridge)
                {
                    priority = 10; // 虚拟网卡兜底
                }

                results.Add(new LocalNetworkEndpoint(name, desc, unicast.Address, priority));
            }
        }

        return results
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.IpAddress.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 获取最适合手机连接的本机局域网主 IPv4 地址
    /// </summary>
    public static IPAddress GetPrimaryLocalIPv4()
    {
        var endpoints = GetLocalIPv4Endpoints();
        if (endpoints.Count > 0)
        {
            return endpoints[0].IpAddress;
        }
        return IPAddress.Loopback;
    }

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
