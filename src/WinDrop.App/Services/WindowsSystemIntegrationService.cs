using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using WinDrop.Core.Services;

namespace WinDrop.App.Services;

/// <summary>
/// Windows 系统集成：右键菜单「通过 WinDrop 分享」、防火墙放行与开机静默自启
/// </summary>
public sealed class WindowsSystemIntegrationService : ISystemIntegrationService
{
    private const string FirewallRuleName = "WinDrop Local Bridge";
    private const string FileShellKey = @"Software\Classes\*\shell\WinDrop";
    private const string DirShellKey = @"Software\Classes\Directory\shell\WinDrop";
    private const string RunStartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    public bool IsFirewallRuleInstalled(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall show rule name=\"{FirewallRuleName}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
                return false;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return proc.ExitCode == 0 && output.Contains(port.ToString(), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public async Task<(bool Success, string Message)> EnsureFirewallRuleAsync(int port)
    {
        try
        {
            string cmdArgs =
                $"advfirewall firewall delete rule name=\"{FirewallRuleName}\" & " +
                $"netsh advfirewall firewall delete rule name=\"{FirewallRuleName} mDNS\" & " +
                $"netsh advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={port} profile=private,domain,public & " +
                $"netsh advfirewall firewall add rule name=\"{FirewallRuleName} mDNS\" dir=in action=allow protocol=UDP localport=5353 profile=private,domain,public";

            bool isAdmin = IsAdministrator();
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c netsh {cmdArgs}",
                UseShellExecute = !isAdmin,
                Verb = isAdmin ? string.Empty : "runas",
                CreateNoWindow = isAdmin,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return (false, "无法启动防火墙配置命令。");

            await proc.WaitForExitAsync();
            return proc.ExitCode == 0
                ? (true, $"已成功放行 TCP {port} 与 UDP 5353（专用+公用网络）！")
                : (false, "未完成防火墙放行，请允许管理员权限确认。");
        }
        catch (Exception ex)
        {
            return (false, $"防火墙配置取消：{ex.Message}");
        }
    }

    public bool IsContextMenuRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(FileShellKey);
        return key != null;
    }

    public (bool Success, string Message) RegisterContextMenu(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return (false, "未能定位当前程序路径。");
        }

        try
        {
            RegisterShellEntry(FileShellKey, executablePath);
            RegisterShellEntry(DirShellKey, executablePath);
            NotifyShellIconChanged();
            return (true, "已注册右键菜单「通过 WinDrop 分享」");
        }
        catch (Exception ex)
        {
            return (false, $"注册右键菜单失败：{ex.Message}");
        }
    }

    private static void RegisterShellEntry(string subKeyPath, string exePath)
    {
        using var shellKey = Registry.CurrentUser.CreateSubKey(subKeyPath);
        shellKey.SetValue(string.Empty, "通过 WinDrop 分享");

        // 优先指向独立的 app.ico 文件以绕过资源管理器对旧 EXE 的空白图标缓存，同时兼容 EXE 内嵌图标
        string exeDir = Path.GetDirectoryName(exePath) ?? string.Empty;
        string standaloneIco = Path.Combine(exeDir, "Assets", "app.ico");
        string iconValue = File.Exists(standaloneIco)
            ? $"\"{standaloneIco}\",0"
            : $"\"{exePath}\",0";

        shellKey.SetValue("Icon", iconValue);

        using var cmdKey = shellKey.CreateSubKey("command");
        cmdKey.SetValue(string.Empty, $"\"{exePath}\" --send \"%1\"");
    }

    public (bool Success, string Message) UnregisterContextMenu()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(FileShellKey, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(DirShellKey, throwOnMissingSubKey: false);
            NotifyShellIconChanged();
            return (true, "已移除右键菜单");
        }
        catch (Exception ex)
        {
            return (false, $"移除失败：{ex.Message}");
        }
    }

    private static void NotifyShellIconChanged()
    {
        try
        {
            const int SHCNE_ASSOCCHANGED = 0x08000000;
            const uint SHCNF_IDLIST = 0x0000;
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    public void SetStartupEnabled(bool enabled, string executablePath)
    {
        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunStartupKey);
            if (enabled && File.Exists(executablePath))
            {
                runKey.SetValue("WinDrop", $"\"{executablePath}\" --minimized");
            }
            else
            {
                runKey.DeleteValue("WinDrop", throwOnMissingValue: false);
            }
        }
        catch { }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
