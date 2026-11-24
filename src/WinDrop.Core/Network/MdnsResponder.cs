using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace WinDrop.Core.Network;

/// <summary>
/// 纯 C# 零依赖 RFC 6762 mDNS 响应服务
/// 直接使用本机计算机名 (<计算机名>.local) 广播与响应 A 记录，无需安装 Apple Bonjour
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
    private int _servicePort;

    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoopTask;
    private Task? _announceLoopTask;

    public string PrimaryDomain { get; }
    public IReadOnlyCollection<string> AllDomains => _hostFqdns.Select(h => h.TrimEnd('.')).ToList();
    public bool IsRunning { get; private set; }

    public MdnsResponder(int servicePort, string? overrideHostName = null)
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
    }

    public void UpdatePort(int newPort)
    {
        _servicePort = newPort;
        _ = SendAnnouncementAsync();
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

            JoinMulticastOnAllInterfaces(client.Client);

            _udpClient = client;
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

    private static void JoinMulticastOnAllInterfaces(Socket socket)
    {
        bool joinedAny = false;
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

        if (!joinedAny)
        {
            socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                new MulticastOption(MulticastAddressIPv4));
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
                if (_udpClient != null)
                {
                    JoinMulticastOnAllInterfaces(_udpClient.Client);
                }
                await SendAnnouncementAsync();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        if (_udpClient == null)
            return;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var received = await _udpClient.ReceiveAsync(token);
                if (TryBuildResponseForQuery(received.Buffer, NetworkHelper.GetPrimaryLocalIPv4(), out byte[]? response) &&
                    response != null)
                {
                    await _udpClient.SendAsync(response, MulticastEndpointIPv4, token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }
        }
    }

    public async Task SendAnnouncementAsync()
    {
        if (_udpClient == null || !IsRunning)
            return;

        try
        {
            IPAddress ip = NetworkHelper.GetPrimaryLocalIPv4();
            if (Equals(ip, IPAddress.Loopback))
                return;

            byte[] packet = BuildAnnouncementPacket(ip);
            await _udpClient.SendAsync(packet, MulticastEndpointIPv4);
        }
        catch { }
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

    private static string SanitizeHostLabel(string raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        var sb = new StringBuilder();
        foreach (char c in raw.Trim().ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')
                sb.Append(c);
            else if (c == '_' || c == ' ')
                sb.Append('-');
        }
        string s = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(s) ? fallback : s;
    }

    public async ValueTask DisposeAsync()
    {
        IsRunning = false;
        if (_cts != null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }
        _udpClient?.Dispose();
        _udpClient = null;
        if (_receiveLoopTask != null) { try { await _receiveLoopTask; } catch { } }
        if (_announceLoopTask != null) { try { await _announceLoopTask; } catch { } }
    }
}
