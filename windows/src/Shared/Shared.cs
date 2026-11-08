using System.Diagnostics;
using System.Reflection;

namespace ClashDeckSetup;

internal static class SetupMain
{
    public const string AppName = "折跃";
    public const string AppVersion = "2.6.15";
    public const string AppId = "ClashDeck";
    public const string AppExe = "ClashDeck.exe";
    public const string Publisher = "自用构建";

    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ClashDeck");

    public static string[] Args { get; private set; } = Array.Empty<string>();

    public static void SetArgs(string[] args) => Args = args ?? Array.Empty<string>();

    /// <summary>net48 没有 Environment.ProcessPath，用主模块路径代替。</summary>
    public static string SelfPath()
    {
        try
        {
            using (var p = Process.GetCurrentProcess())
                return p.MainModule != null ? p.MainModule.FileName : null;
        }
        catch { return null; }
    }

    /// <summary>net48 没有 Math.Clamp。</summary>
    public static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    /// <summary>net48 用 Win32 声明 DPI 感知，避免高分屏下界面发虚。</summary>
    public static void EnableDpiAwareness()
    {
        try { SetProcessDPIAware(); } catch { }
    }

    /// <summary>
    /// %TEMP% 里的副本专用：接手删掉安装目录里剩下的东西（主要是被占用过的 uninstall.exe）。
    /// 命中时直接结束进程，不进界面。
    /// </summary>
    public static bool HandleFinishUninstallRequest()
    {
        var target = Value("--finish-uninstall");
        if (target == null) return false;
        Installer.FinishDelete(target, Flag("--purge"));
        return true;
    }

    public static bool Flag(string name) =>
        Args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    public static string Value(string name)
    {
        for (int i = 0; i < Args.Length - 1; i++)
            if (string.Equals(Args[i], name, StringComparison.OrdinalIgnoreCase))
                return Args[i + 1];
        for (int i = 0; i < Args.Length; i++)
        {
            var a = Args[i];
            if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return a.Substring(name.Length + 1).Trim('"');
        }
        return null;
    }
}

internal static class Log
{
    private static readonly object Gate = new();
    private static string _file;

    public static string File
    {
        get
        {
            if (_file != null) return _file;
            try
            {
                _file = Path.Combine(Path.GetTempPath(), "ClashDeckSetup.log");
            }
            catch { _file = null; }
            return _file;
        }
    }

    public static void Write(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {msg}";
        Debug.WriteLine(line);
        try
        {
            if (File == null) return;
            lock (Gate) System.IO.File.AppendAllText(File, line + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>安装/卸载日志也留一份在安装目录里，事后能查。</summary>
    public static void WriteTo(string dir, string msg)
    {
        Write(msg);
        try
        {
            if (string.IsNullOrEmpty(dir)) return;
            var logs = Path.Combine(dir, "logs");
            Directory.CreateDirectory(logs);
            lock (Gate)
                System.IO.File.AppendAllText(Path.Combine(logs, "setup.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
