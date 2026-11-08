using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace SysProxy;

/// <summary>
/// Windows 系统代理（WinINet）开关。
/// 只写 HKCU 当前用户键，不需要管理员；改完必须通知 WinINet，
/// 否则浏览器要等下次读设置才生效。
/// 用法: sysproxy status | on [host:port] | off
/// </summary>
internal static class Program
{
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int INTERNET_OPTION_REFRESH = 37;
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_PER_CONNECTION_OPTION = 75;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private const string DefaultBypass = "<local>;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;localhost;::1";

    private static int Main(string[] args)
    {
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        try
        {
            switch (cmd)
            {
                case "status":
                    Console.WriteLine(StatusJson());
                    return 0;
                case "on":
                    var server = args.Length > 1 ? args[1] : "127.0.0.1:7897";
                    Enable(server);
                    Notify();
                    Console.WriteLine(StatusJson());
                    return 0;
                case "off":
                    Disable();
                    Notify();
                    Console.WriteLine(StatusJson());
                    return 0;
                default:
                    Console.Error.WriteLine("usage: sysproxy status|on [host:port]|off");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static void Enable(string server)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)
                        ?? throw new InvalidOperationException("打不开 Internet Settings 注册表键");
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", server, RegistryValueKind.String);
        var existing = key.GetValue("ProxyOverride") as string;
        if (string.IsNullOrWhiteSpace(existing))
            key.SetValue("ProxyOverride", DefaultBypass, RegistryValueKind.String);
    }

    private static string BackupPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "proxy-backup.txt");


    private static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)
                        ?? throw new InvalidOperationException("打不开 Internet Settings 注册表键");
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        // 关掉就清理，不做"恢复"：留着指向本软件端口的地址是死值 ——
        // 内核已经停了，任何程序读到它都连不上。清掉之后系统回到"从未配置过代理"的状态。
        key.DeleteValue("ProxyServer", false);
        key.DeleteValue("ProxyOverride", false);
        try { if (File.Exists(BackupPath)) File.Delete(BackupPath); } catch { }   // 兼容旧版本留下的备份
    }

    private static string StatusJson()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: false);
        var enabled = key?.GetValue("ProxyEnable") is int i && i != 0;
        var server = key?.GetValue("ProxyServer") as string ?? "";
        var pac = key?.GetValue("AutoConfigURL") as string ?? "";
        return $"{{\"enabled\":{(enabled ? "true" : "false")},\"server\":\"{Esc(server)}\",\"pac\":\"{Esc(pac)}\"}}";
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>通知 WinINet 设置已变更，让已打开的浏览器立刻重读代理配置。</summary>
    private static void Notify()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        // 兜底：广播 WM_SETTINGCHANGE，部分程序只听这个
        SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings",
            0x0002, 1000, out _);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
