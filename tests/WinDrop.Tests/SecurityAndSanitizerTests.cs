using WinDrop.Core.Security;
using WinDrop.Core.Services;
using Xunit;

namespace WinDrop.Tests;

public sealed class SecurityAndSanitizerTests
{
    [Theory]
    [InlineData("../../Windows/System32/cmd.exe", "cmd.exe")]
    [InlineData(@"..\..\evil\payload.bat", "payload.bat")]
    [InlineData(@"C:\Users\Admin\secret.txt", "secret.txt")]
    [InlineData("photo.jpg:$DATA", "photo.jpg")]
    [InlineData("archive.zip:Zone.Identifier", "archive.zip")]
    [InlineData("截图 12:30:45.png", "截图 12_30_45.png")]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("NUL.tar.gz", "_NUL.tar.gz")]
    [InlineData("COM1", "_COM1")]
    [InlineData("LPT9.pdf", "_LPT9.pdf")]
    [InlineData("hello<>:\"|?*.png", "hello_______.png")]
    [InlineData("   ...", "received_file")]
    public void Sanitize_BlocksTraversalAdsAndReservedNames(string rawInput, string expected)
    {
        string actual = FileNameSanitizer.Sanitize(rawInput);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveUniquePath_AppendsIncrementingSuffixOnCollision()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WinDropTest_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            string p1 = FileNameSanitizer.ResolveUniquePath(tempDir, "IMG_0001.HEIC");
            File.WriteAllText(p1, "1");

            string p2 = FileNameSanitizer.ResolveUniquePath(tempDir, "IMG_0001.HEIC");
            File.WriteAllText(p2, "2");

            string p3 = FileNameSanitizer.ResolveUniquePath(tempDir, "IMG_0001.HEIC");

            Assert.Equal("IMG_0001.HEIC", Path.GetFileName(p1));
            Assert.Equal("IMG_0001 (1).HEIC", Path.GetFileName(p2));
            Assert.Equal("IMG_0001 (2).HEIC", Path.GetFileName(p3));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void StateService_OnlyAllowsExplicitlySharedOrCopiedFilesForDownload()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WinDropAuthFile_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            string sharedFile = Path.Combine(tempDir, "shared.pdf");
            string secretFile = Path.Combine(tempDir, "secret.key");
            File.WriteAllText(sharedFile, "public");
            File.WriteAllText(secretFile, "private");

            var state = new WinDropStateService(Path.Combine(tempDir, "data"));
            state.AuthorizeFilesForDownload(new[] { sharedFile });

            Assert.True(state.IsFileAuthorizedForDownload(sharedFile, out _));
            Assert.False(state.IsFileAuthorizedForDownload(secretFile, out _));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
