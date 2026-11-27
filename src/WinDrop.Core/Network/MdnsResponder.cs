using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace WinDrop.Core.Network;

/// <summary>
/// 纯 C# 零依赖 RFC 6762 mDNS 响应服务
/// 针对多网卡 / 代理 TUN 模式 / 公共网络增强：
/// 1. 仅在非 TUN 真实物理/热点网卡加入组播并绑定按接口发送通道，绕过 TUN 低跃点路由劫持；
/// 2. 收到查询时按来源子网动态匹配本机 IP，并采用「单播直回 + 接口组播」双发策略穿透受限 Wi-Fi；
/// 3. 监听系统网络变化自动热重载通道，实时响应热点开关或网络切换。
/// </summary>
public sealed class MdnsResponder : IAsyncDisposable
{
    private static readonly IPAddress MulticastAddressIPv4 = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint MulticastEndpointIPv4 = new(MulticastAddressIPv4, 5353);

    private const ushort TypeA = 1;
    private const ushort TypePtr = 12;
    private const ushort TypeTxt = 16;
    private const ushort TypeSrv = 33;
    private const ushort TypeAny = 255;

    private readonly HashSet<string> _hostFqdns = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _primaryHostFqdn;
    private readonly string _serviceTypeFqdn = "_http._tcp.local.";
    private readonly string _instanceFqdn;
    private readonly Func<string?>? _preferredIpProvider;
    private readonly object _channelLock = new();
    private readonly Dictionary<string, OutboundInterfaceChannel> _outboundChannels = new(StringComparer.Ordinal);

    private int _servicePort;
    private UdpClient? _receiveClient;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoopTask;
    private Task? _announceLoopTask;
    private int _refreshPending;

    public string PrimaryDomain { get; }
    public IReadOnlyCollection<string> AllDomains => _hostFqdns.Select(h => h.TrimEnd('.')).ToList();
    public bool IsRunning { get; private set; }

    private sealed record OutboundInterfaceChannel(LocalNetworkEndpoint Endpoint, UdpClient Client);

    public MdnsResponder(int servicePort, string? overrideHostName = null, Func<string?>? preferredIpProvider = null)
    {
        string hostName = string.IsNullOrWhiteSpace(overrideHostName)
            ? Dns.GetHostName()
            : overrideHostName.Trim();

        hostName = hostName.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(hostName))
        {
            hostName = Environment.MachineName;
        }

        PrimaryDomain = $"{hostName}.local";
        _primaryHostFqdn = $"{PrimaryDomain}.";
        _hostFqdns.Add(_primaryHostFqdn);

        _instanceFqdn = $"{hostName}.{_serviceTypeFqdn}";
        _servicePort = servicePort;
        _preferredIpProvider = preferredIpProvider;
    }

    public void UpdatePort(int newPort)
    {
        _servicePort = newPort;
        _ = SendAnnouncementAsync();
    }

    public void RequestInterfaceRefresh()
    {
        if (!IsRunning)
            return;

        if (Interlocked.Exchange(ref _refreshPending, 1) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500);
                if (IsRunning)
                {
                    RebuildInterfaceChannels();
                    await SendAnnouncementAsync();
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _refreshPending, 0);
            }
        });
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            return Task.CompletedTask;

        try
        {
            var client = new UdpClient();
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.ExclusiveAddressUse = false;
            client.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));

            _receiveClient = client;
            RebuildInterfaceChannels();

            NetworkChange.NetworkAddressChanged += OnNetworkEnvironmentChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            IsRunning = true;

            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
            _announceLoopTask = Task.Run(() => PeriodicAnnounceLoopAsync(_cts.Token), CancellationToken.None);
        }
        catch
        {
            IsRunning = false;
        }

        return Task.CompletedTask;
    }

    private void OnNetworkEnvironmentChanged(object? sender, EventArgs e) => RequestInterfaceRefresh();
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => RequestInterfaceRefresh();

    private void RebuildInterfaceChannels()
    {
        if (_receiveClient == null)
            return;

        var eligible = NetworkHelper.GetEligibleLanEndpoints(_preferredIpProvider?.Invoke());
        JoinMulticastOnEligibleInterfaces(_receiveClient.Client, eligible);

        lock (_channelLock)
        {
            var nextKeys = eligible.Select(e => e.IpAddress.ToString()).ToHashSet(StringComparer.Ordinal);
            foreach (string existingKey in _outboundChannels.Keys.ToList())
            {
                if (!nextKeys.Contains(existingKey))
                {
                    _outboundChannels[existingKey].Client.Dispose();
                    _outboundChannels.Remove(existingKey);
                }
            }

            foreach (var ep in eligible)
            {
                string key = ep.IpAddress.ToString();
                if (_outboundChannels.ContainsKey(key))
                {
                    _outboundChannels[key] = _outboundChannels[key] with { Endpoint = ep };
                    continue;
                }

                UdpClient? nicSender = TryCreateInterfaceSender(ep.IpAddress);
                if (nicSender != null)
                {
                    _outboundChannels[key] = new OutboundInterfaceChannel(ep, nicSender);
                }
            }
        }
    }

    private static UdpClient? TryCreateInterfaceSender(IPAddress localIp)
    {
        foreach (int bindPort in new[] { 5353, 0 })
        {
            UdpClient? sender = null;
            try
            {
                sender = new UdpClient(AddressFamily.InterNetwork);
                sender.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                sender.ExclusiveAddressUse = false;
                sender.Client.Bind(new IPEndPoint(localIp, bindPort));
                // 强制 IPv4 组播从当前物理/热点网卡发出，彻底避开代理 TUN 低跃点默认路由劫持
                sender.Client.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.MulticastInterface,
                    localIp.GetAddressBytes());
                sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                return sender;
            }
            catch
            {
                sender?.Dispose();
            }
        }

        return null;
    }

    private static void JoinMulticastOnEligibleInterfaces(Socket socket, IReadOnlyList<LocalNetworkEndpoint> eligible)
    {
        bool joinedAny = false;
        foreach (var ep in eligible)
        {
            try
            {
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.AddMembership,
                    new MulticastOption(MulticastAddressIPv4, ep.IpAddress));
                joinedAny = true;
            }
            catch { }
        }

        if (!joinedAny)
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    !nic.SupportsMulticast)
                    continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    try
                    {
                        socket.SetSocketOption(
                            SocketOptionLevel.IP,
                            SocketOptionName.AddMembership,
                            new MulticastOption(MulticastAddressIPv4, ua.Address));
                        joinedAny = true;
                    }
                    catch { }
                }
            }
        }

        if (!joinedAny)
        {
            try
            {
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.AddMembership,
                    new MulticastOption(MulticastAddressIPv4));
            }
            catch { }
        }
    }

    private async Task PeriodicAnnounceLoopAsync(CancellationToken token)
    {
        await SendAnnouncementAsync();
        try
        {
            await Task.Delay(1000, token);
            await SendAnnouncementAsync();
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                RebuildInterfaceChannels();
                await SendAnnouncementAsync();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        if (_receiveClient == null)
            return;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var received = await _receiveClient.ReceiveAsync(token);
                var eligible = NetworkHelper.GetEligibleLanEndpoints(_preferredIpProvider?.Invoke());
                IPAddress replyIp = NetworkHelper.SelectBestIpForRemotePeer(
                    received.RemoteEndPoint.Address,
                    eligible,
                    _preferredIpProvider?.Invoke());

                if (Equals(replyIp, IPAddress.Loopback))
                    continue;

                if (TryBuildResponseForQuery(received.Buffer, replyIp, out byte[]? response) &&
                    response != null)
                {
                    await SendResponseDualPathAsync(replyIp, received.RemoteEndPoint, response, token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }
        }
    }

    private async Task SendResponseDualPathAsync(IPAddress replyIp, IPEndPoint remoteEndPoint, byte[] response, CancellationToken token)
    {
        UdpClient? nicSender = null;
        lock (_channelLock)
        {
            if (_outboundChannels.TryGetValue(replyIp.ToString(), out var ch))
            {
                nicSender = ch.Client;
            }
        }

        UdpClient activeSender = nicSender ?? _receiveClient!;
        // 1. 先直接单播回复发起请求的手机端（即使公共 Wi-Fi 丢弃路由器组播也能直达）
        try
        {
            if (remoteEndPoint.Address.AddressFamily == AddressFamily.InterNetwork &&
                !Equals(remoteEndPoint.Address, IPAddress.Any))
            {
                await activeSender.SendAsync(response, remoteEndPoint, token);
            }
        }
        catch { }

        // 2. 再通过对应物理网卡发出组播响应，刷新局域网 mDNS 缓存
        try
        {
            await activeSender.SendAsync(response, MulticastEndpointIPv4, token);
        }
        catch { }
    }

    public async Task SendAnnouncementAsync()
    {
        if (_receiveClient == null || !IsRunning)
            return;

        List<OutboundInterfaceChannel> snapshot;
        lock (_channelLock)
        {
            snapshot = _outboundChannels.Values.ToList();
        }

        if (snapshot.Count == 0)
        {
            try
            {
                IPAddress ip = NetworkHelper.GetPrimaryLocalIPv4(_preferredIpProvider?.Invoke());
                if (!Equals(ip, IPAddress.Loopback))
                {
                    byte[] packet = BuildAnnouncementPacket(ip);
                    await _receiveClient.SendAsync(packet, MulticastEndpointIPv4);
                }
            }
            catch { }
            return;
        }

        // 在每个真实物理/热点网卡上分别广播自身局域网 IP，互不干扰、无视 TUN 路由
        foreach (var channel in snapshot)
        {
            try
            {
                byte[] packet = BuildAnnouncementPacket(channel.Endpoint.IpAddress);
                await channel.Client.SendAsync(packet, MulticastEndpointIPv4);
            }
            catch { }
        }
    }

    public bool TryBuildResponseForQuery(ReadOnlySpan<byte> packet, IPAddress localIpv4, out byte[]? responseBytes)
    {
        responseBytes = null;
        if (packet.Length < 12)
            return false;

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        if ((flags & 0x8000) != 0)
            return false;

        ushort qdCount = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(4, 2));
        if (qdCount == 0)
            return false;

        int offset = 12;
        var matchedHosts = new List<string>();
        bool needServicePtr = false;

        for (int i = 0; i < qdCount; i++)
        {
            if (!TryReadDomainName(packet, ref offset, out string qName))
                return false;
            if (offset + 4 > packet.Length)
                return false;

            ushort qType = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset, 2));
            offset += 4;

            if (_hostFqdns.Contains(qName) && qType is TypeA or TypeAny)
            {
                matchedHosts.Add(qName);
            }
            else if (string.Equals(qName, _serviceTypeFqdn, StringComparison.OrdinalIgnoreCase) && qType is TypePtr or TypeAny)
            {
                needServicePtr = true;
            }
        }

        if (matchedHosts.Count == 0 && !needServicePtr)
            return false;

        var answers = new List<byte[]>();
        if (needServicePtr)
        {
            answers.Add(BuildResourceRecord(_serviceTypeFqdn, TypePtr, false, 120, EncodeDomainName(_instanceFqdn)));
            answers.Add(BuildSrvRecord(_instanceFqdn, (ushort)_servicePort, _primaryHostFqdn, 120));
            answers.Add(BuildResourceRecord(_primaryHostFqdn, TypeA, true, 120, localIpv4.GetAddressBytes()));
        }

        foreach (string h in matchedHosts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            answers.Add(BuildResourceRecord(h, TypeA, true, 120, localIpv4.GetAddressBytes()));
        }

        responseBytes = PackAnswers(answers);
        return true;
    }

    public byte[] BuildAnnouncementPacket(IPAddress localIpv4)
    {
        var answers = new List<byte[]>
        {
            BuildResourceRecord(_serviceTypeFqdn, TypePtr, false, 120, EncodeDomainName(_instanceFqdn)),
            BuildSrvRecord(_instanceFqdn, (ushort)_servicePort, _primaryHostFqdn, 120)
        };
        foreach (string host in _hostFqdns)
        {
            answers.Add(BuildResourceRecord(host, TypeA, true, 120, localIpv4.GetAddressBytes()));
        }
        return PackAnswers(answers);
    }

    private static byte[] PackAnswers(List<byte[]> answers)
    {
        using var ms = new MemoryStream(256);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header[0..2], 0);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..4], 0x8400); // QR=1, AA=1
        BinaryPrimitives.WriteUInt16BigEndian(header[6..8], (ushort)answers.Count);
        ms.Write(header);
        foreach (var ans in answers)
            ms.Write(ans);
        return ms.ToArray();
    }

    private static byte[] BuildSrvRecord(string instanceFqdn, ushort port, string targetHostFqdn, uint ttl)
    {
        byte[] targetBytes = EncodeDomainName(targetHostFqdn);
        byte[] rdata = new byte[6 + targetBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(rdata.AsSpan(4, 2), port);
        Buffer.BlockCopy(targetBytes, 0, rdata, 6, targetBytes.Length);
        return BuildResourceRecord(instanceFqdn, TypeSrv, true, ttl, rdata);
    }

    private static byte[] BuildResourceRecord(string nameFqdn, ushort type, bool cacheFlush, uint ttl, byte[] rdata)
    {
        byte[] nameBytes = EncodeDomainName(nameFqdn);
        byte[] record = new byte[nameBytes.Length + 10 + rdata.Length];
        Buffer.BlockCopy(nameBytes, 0, record, 0, nameBytes.Length);
        int p = nameBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(p, 2), type);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(p + 2, 2), (ushort)(0x0001 | (cacheFlush ? 0x8000 : 0x0000)));
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(p + 4, 4), ttl);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(p + 8, 2), (ushort)rdata.Length);
        Buffer.BlockCopy(rdata, 0, record, p + 10, rdata.Length);
        return record;
    }

    public static byte[] EncodeDomainName(string fqdn)
    {
        using var ms = new MemoryStream();
        string trimmed = fqdn.Trim().TrimEnd('.');
        foreach (string label in trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(label);
            int len = Math.Min(63, bytes.Length);
            ms.WriteByte((byte)len);
            ms.Write(bytes, 0, len);
        }
        ms.WriteByte(0);
        return ms.ToArray();
    }

    public static bool TryReadDomainName(ReadOnlySpan<byte> packet, ref int offset, out string domainName)
    {
        domainName = string.Empty;
        var sb = new StringBuilder();
        int current = offset;
        bool jumped = false;
        int jumpCount = 0;

        while (true)
        {
            if (current >= packet.Length)
                return false;

            byte len = packet[current];
            if (len == 0)
            {
                if (!jumped)
                    offset = current + 1;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (current + 1 >= packet.Length || ++jumpCount > 12)
                    return false;
                int pointer = ((len & 0x3F) << 8) | packet[current + 1];
                if (!jumped)
                {
                    offset = current + 2;
                    jumped = true;
                }
                current = pointer;
                continue;
            }

            current++;
            if (current + len > packet.Length)
                return false;

            sb.Append(Encoding.UTF8.GetString(packet.Slice(current, len))).Append('.');
            current += len;
        }

        domainName = sb.Length == 0 ? "." : sb.ToString();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        IsRunning = false;
        NetworkChange.NetworkAddressChanged -= OnNetworkEnvironmentChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;

        if (_cts != null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }

        _receiveClient?.Dispose();
        _receiveClient = null;

        lock (_channelLock)
        {
            foreach (var ch in _outboundChannels.Values)
            {
                ch.Client.Dispose();
            }
            _outboundChannels.Clear();
        }

        if (_receiveLoopTask != null) { try { await _receiveLoopTask; } catch { } }
        if (_announceLoopTask != null) { try { await _announceLoopTask; } catch { } }
    }
}
