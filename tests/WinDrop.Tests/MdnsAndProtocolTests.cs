using System.Buffers.Binary;
using System.Net;
using System.Text;
using WinDrop.Core.Network;
using WinDrop.Core.Services;
using Xunit;

namespace WinDrop.Tests;

public sealed class MdnsAndProtocolTests
{
    [Fact]
    public void MdnsResponder_UsesMachineHostNameLocalAndRespondsToARecord()
    {
        string machineName = Dns.GetHostName();
        var defaultResponder = new MdnsResponder(58446);
        Assert.Equal($"{machineName}.local", defaultResponder.PrimaryDomain);

        var testIp = IPAddress.Parse("192.168.1.66");
        byte[] q1 = BuildDnsQuery($"{machineName}.local.", qType: 1);
        Assert.True(defaultResponder.TryBuildResponseForQuery(q1, testIp, out byte[]? r1));
        Assert.Equal(new byte[] { 192, 168, 1, 66 }, r1![^4..]);
    }

    [Fact]
    public void Base64FilePath_RoundTripsChinesePathsWithSlashesInBase64()
    {
        // 包含中文字符的深层路径编码为标准 Base64 时常含有 '/'，验证完整还原
        string original = @"D:\测试目录\照片分享\2026年旅行原图_?>测试.HEIC";
        string encoded = QrAndPathHelper.EncodeFilePathBase64(original);
        string? decoded = QrAndPathHelper.DecodeFilePathBase64(encoded);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void ShortcutBuilder_GeneratesSendAndReceiveTemplates()
    {
        string sendXml = Encoding.UTF8.GetString(ShortcutBuilderService.BuildSendToWindowsShortcutPlist("windrop.local", 58446, "abc123"));
        string recvXml = Encoding.UTF8.GetString(ShortcutBuilderService.BuildReceiveFromWindowsShortcutPlist("windrop.local", 58446, "abc123"));

        Assert.Contains("http://windrop.local:58446/v2/send", sendXml);
        Assert.Contains("Authorization", sendXml);
        Assert.Contains("http://windrop.local:58446/v2/receive", recvXml);
    }

    private static byte[] BuildDnsQuery(string fqdn, ushort qType)
    {
        using var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header[4..6], 1);
        ms.Write(header);
        ms.Write(MdnsResponder.EncodeDomainName(fqdn));
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail[0..2], qType);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..4], 1);
        ms.Write(tail);
        return ms.ToArray();
    }
}
