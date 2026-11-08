using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace ClashDeckSetup;

internal sealed class InstallResult
{
    public bool Ok { get; private set; }
    public string Message { get; private set; }
    /// <summary>这台机器是否缺 .NET Framework 4.8（主程序是 net48，缺了打不开）。</summary>
    public bool Net48Missing { get; set; }
    public InstallResult(bool ok, string message) { Ok = ok; Message = message; }
}

internal static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClashDeck";
    private const string PayloadResource = "payload.zip";

    /// <summary>记着"这一版装了哪些文件"，供下次安装算废弃文件用。放在安装目录根。</summary>
    private const string PayloadManifest = ".payload-manifest.txt";

    /// <summary>自启脚本的位置（与 server.js 的 AUTOSTART_VBS 必须一致）。</summary>
    private static string AutostartVbs => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "ClashDeck.vbs");

    public static string InstalledDir
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
                var dir = k?.GetValue("InstallLocation") as string;
                return string.IsNullOrWhiteSpace(dir) ? null : dir;
            }
            catch { return null; }
        }
    }

    public static string InstalledVersion
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
                return k?.GetValue("DisplayVersion") as string;
            }
            catch { return null; }
        }
    }

    // ---------------------------------------------------------------- 安装

    public static InstallResult Install(string targetDir, bool desktopShortcut,
        Action<int, string> progress = null)
    {
        void Report(int pct, string msg) { progress?.Invoke(pct, msg); Log.Write($"[{pct}%] {msg}"); }

        try
        {
            if (string.IsNullOrWhiteSpace(targetDir))
                return new InstallResult(false, "安装目录不能为空");
            targetDir = Path.GetFullPath(targetDir.Trim().TrimEnd('\\'));

            // 1) 目标目录可能被占用
            var running = RunningApps(targetDir);
            if (running.Count > 0)
            {
                Report(0, "发现正在运行的 折跃，先结束它");
                foreach (var p in running)
                {
                    try { p.Kill(); p.WaitForExit(4000); } catch (Exception ex) { Log.Write("kill: " + ex.Message); }
                }
                Thread.Sleep(600);
            }

            Directory.CreateDirectory(targetDir);

            // 2) 准备载荷
            Report(2, "解压安装包…");
            var zipPath = Path.Combine(Path.GetTempPath(), $"clashdeck-payload-{Guid.NewGuid():N}.zip");
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource))
            {
                if (res is null) return new InstallResult(false, "安装包损坏：缺少内置载荷");
                using var fs = File.Create(zipPath);
                res.CopyTo(fs);
            }

            int written = 0;
            long bytes = 0;
            // 本次载荷里都有哪些文件（相对路径，正斜杠）。用来算"上一版有、这一版没有"的废弃文件。
            var newFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                var fresh = !File.Exists(Path.Combine(targetDir, "data", "config.json"));
                var total = entries.Sum(e => Math.Max(1L, e.Length));
                long done = 0;

                foreach (var e in entries)
                {
                    // 升级安装时不覆盖用户数据（配置、订阅、流量统计）
                    if (!fresh && e.FullName.Replace('\\', '/').StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                    {
                        done += Math.Max(1L, e.Length);
                        continue;
                    }

                    var dest = Path.GetFullPath(Path.Combine(targetDir, e.FullName));
                    if (!dest.StartsWith(targetDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(dest, targetDir, StringComparison.OrdinalIgnoreCase))
                        continue;   // 防目录穿越

                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, overwrite: true);
                    newFiles.Add(e.FullName.Replace('\\', '/'));
                    written++;
                    bytes += e.Length;
                    done += Math.Max(1L, e.Length);
                    var pct = 2 + (int)(done * 88.0 / total);
                    if (written % 40 == 0 || e.Length > 5_000_000)
                        Report(Math.Min(90, pct), $"写入 {e.Name}（{(e.Length / 1024.0 / 1024.0):F1} MB）");
                }
                Log.Write($"extracted {written} files, {bytes / 1024 / 1024} MB, fresh={fresh}");
            }
            finally
            {
                try { File.Delete(zipPath); } catch { }
            }

            // 2.5) 清理上一版废弃文件。
            //
            // 以前的安装只是"解压覆盖"，从 2.0 → 2.6 一路升级下来，被删掉的
            // public/app.js、旧版内核、重组前的目录结构全都留着不动 ——
            // 安装目录只增不减，而且没人能说清哪些文件是活的。
            //
            // 做法是记清单而不是"清空目录"：每次安装把本次载荷的文件列表写进
            // .payload-manifest.txt，下次安装读它，只删「上次有、这次没有」的那些。
            // 这样用户自己塞进安装目录的文件、以及 data/ 下的全部内容都不会被动。
            // 第一次带清单的版本（2.6.7）没有旧清单，因此只写不删，绝无误删风险。
            try
            {
                var manifestPath = Path.Combine(targetDir, PayloadManifest);
                var oldFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(manifestPath))
                {
                    foreach (var line in File.ReadAllLines(manifestPath))
                    {
                        var f = line.Trim();
                        if (f.Length > 0) oldFiles.Add(f);
                    }
                }

                int removed = 0;
                if (oldFiles.Count > 0)
                {
                    Report(91, "清理上一版遗留文件…");
                    foreach (var rel in oldFiles)
                    {
                        if (newFiles.Contains(rel)) continue;
                        if (rel.StartsWith("data/", StringComparison.OrdinalIgnoreCase)) continue;
                        var full = Path.GetFullPath(Path.Combine(targetDir, rel));
                        if (!full.StartsWith(targetDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                        try { if (File.Exists(full)) { File.Delete(full); removed++; } } catch { /* 被占用就留着，下次再删 */ }
                    }
                    // 空目录顺手收掉（只删空的，非空说明还有别的东西）
                    foreach (var dir in new[] { "public", "core", "core/resources", "rules", "tools", "runtime" })
                    {
                        var d = Path.Combine(targetDir, dir);
                        try
                        {
                            if (Directory.Exists(d) && Directory.GetFileSystemEntries(d).Length == 0)
                                Directory.Delete(d);
                        }
                        catch { }
                    }
                }

                File.WriteAllLines(manifestPath, newFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                Log.Write($"manifest: old={oldFiles.Count}, new={newFiles.Count}, removed={removed}");
                if (removed > 0) Report(91, $"已清理上一版遗留文件 {removed} 个");
            }
            catch (Exception ex)
            {
                // 清理失败不能挡住安装本身：装着是主功能，清残留是附加收益
                Log.Write("cleanup stale files: " + ex.Message);
            }

            // 3) 卸载器：payload 里自带轻量的 uninstall.exe（约 1 MB）。
            //    万一缺了才用安装包自身兜底 —— 那样会多占安装包同样大的空间。
            Report(92, "登记卸载信息…");
            var uninstaller = Path.Combine(targetDir, "uninstall.exe");
            if (!File.Exists(uninstaller))
            {
                try
                {
                    var self = SetupMain.SelfPath();
                    if (!string.IsNullOrEmpty(self) && self.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(self, uninstaller, overwrite: true);
                        Log.Write("uninstaller missing in payload, copied setup itself");
                    }
                }
                catch (Exception ex) { Log.Write("copy uninstaller: " + ex.Message); }
            }

            // 4) 快捷方式
            var exe = Path.Combine(targetDir, SetupMain.AppExe);
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            // 产品改过名：旧名快捷方式不会因为装一次新版就消失，而且多半指向已经被换掉的目录，
            // 留着就是一个点了没反应的图标。改名时一并清掉。
            CleanupLegacyShortcuts(programs, desktopDir);
            var startMenu = Path.Combine(programs, SetupMain.AppName);
            Directory.CreateDirectory(startMenu);
            Shortcut.Create(Path.Combine(startMenu, SetupMain.AppName + ".lnk"), exe, targetDir,
                "本地代理控制台", exe);
            if (desktopShortcut)
            {
                Shortcut.Create(Path.Combine(desktopDir, SetupMain.AppName + ".lnk"), exe, targetDir, "本地代理控制台", exe);
            }
            else
            {
                try
                {
                    var desk = Path.Combine(desktopDir, SetupMain.AppName + ".lnk");
                    if (File.Exists(desk)) File.Delete(desk);
                }
                catch { }
            }

            // 5) 注册表
            Report(96, "写入注册表…");
            WriteRegistry(targetDir, bytes);

            Report(100, "安装完成");
            Log.WriteTo(targetDir, $"install ok -> {targetDir} ({written} files, {bytes / 1024 / 1024} MB)");
            bool has48 = HasNet48();
            Log.WriteTo(targetDir, ".NET Framework 4.8 present: " + has48);
            return new InstallResult(true, "安装完成") { Net48Missing = !has48 };
        }
        catch (UnauthorizedAccessException)
        {
            return new InstallResult(false, $"没有写入权限：{targetDir}\n请换一个目录，或以管理员身份运行安装程序");
        }
        catch (Exception ex)
        {
            Log.Write("install failed: " + ex);
            return new InstallResult(false, "安装失败：" + ex.Message);
        }
    }

    private static void WriteRegistry(string dir, long sizeBytes)
    {
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey, writable: true);
        if (k is null) throw new IOException("无法写入注册表卸载项");
        var exe = Path.Combine(dir, SetupMain.AppExe);
        k.SetValue("DisplayName", SetupMain.AppName);
        k.SetValue("DisplayVersion", SetupMain.AppVersion);
        k.SetValue("Publisher", SetupMain.Publisher);
        k.SetValue("DisplayIcon", exe + ",0");
        k.SetValue("InstallLocation", dir);
        k.SetValue("UninstallString", $"\"{Path.Combine(dir, "uninstall.exe")}\" --uninstall --dir \"{dir}\"");
        k.SetValue("QuietUninstallString", $"\"{Path.Combine(dir, "uninstall.exe")}\" --uninstall --dir \"{dir}\" --force");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeBytes / 1024), RegistryValueKind.DWord);
        k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    // ---------------------------------------------------------------- 卸载

    public static InstallResult Uninstall(string dir, bool deleteData)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return new InstallResult(false, "安装目录不存在，无需卸载");

            foreach (var p in RunningApps(dir))
            {
                try { p.Kill(); p.WaitForExit(4000); } catch { }
            }
            // 等文件句柄真正释放（node / 内核退出后系统回收句柄有个短暂窗口）
            Thread.Sleep(800);

            // 快捷方式
            SafeDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                SetupMain.AppName + ".lnk"));
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), SetupMain.AppName);
            if (Directory.Exists(startMenu)) SafeDeleteDir(startMenu);

            // 开机自启脚本（%APPDATA%\...\Startup\ClashDeck.vbs）。
            // 以前卸载不碰它：下次开机会执行一个指向已删除 start.cmd 的脚本，
            // Windows 弹"脚本宿主找不到文件"，用户完全不知道是残留。
            try
            {
                if (File.Exists(AutostartVbs))
                {
                    File.Delete(AutostartVbs);
                    Log.Write("removed autostart vbs");
                }
            }
            catch (Exception ex) { Log.Write("del autostart vbs: " + ex.Message); }

            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); }
            catch (Exception ex) { Log.Write("del registry: " + ex.Message); }

            // 删除文件（跳过自身），然后把"删自己"这一步移交给 %TEMP% 里的副本。
            // 早先用 cmd + timeout 延迟删，实测在无控制台环境下 timeout 立即返回，
            // 那时 uninstall.exe 还被锁着，整个 rmdir 就失败了 —— 所以改成自我移交。
            PurgeDirectory(dir, deleteData, keepSelf: true);
            Log.WriteTo(dir, $"uninstall ok, deleteData={deleteData}");

            var self = SetupMain.SelfPath();
            var selfInTarget = !string.IsNullOrEmpty(self) &&
                Path.GetFullPath(self).StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);

            if (selfInTarget)
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"cd-uninst-{Guid.NewGuid():N}.exe");
                File.Copy(self, tmp, overwrite: true);
                var args = $"--finish-uninstall \"{dir}\"" + (deleteData ? " --purge" : "");
                Process.Start(new ProcessStartInfo(tmp, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetTempPath(),
                });
                Log.Write("handing deletion over to temp copy");
            }
            else
            {
                FinishDelete(dir, deleteData);
            }
            return new InstallResult(true, "已卸载");
        }
        catch (Exception ex)
        {
            Log.Write("uninstall failed: " + ex);
            return new InstallResult(false, "卸载失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 从 %TEMP% 里的副本执行最终删除：此时 uninstall.exe 已不被占用，可以整体删掉。
    /// deleteData=false 时保留 data 目录，只把程序文件与卸载器清干净。
    /// </summary>
    public static bool FinishDelete(string dir, bool deleteData)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        var exe = Path.Combine(dir, "uninstall.exe");

        // 关键：当前工作目录如果在安装目录里（用户双击 uninstall.exe 时就是），
        // Windows 会拒绝删除这个目录，目录会剩下一个空壳。先把自己挪到 TEMP。
        try
        {
            var cwd = Directory.GetCurrentDirectory();
            if (cwd.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
                Directory.SetCurrentDirectory(Path.GetTempPath());
        }
        catch { }

        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (!Directory.Exists(dir)) { CleanTempCopies(); return true; }

                foreach (var f in Directory.GetFiles(dir))
                {
                    if (string.Equals(f, exe, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(f); } catch { }
                }

                // 原进程已退出，这一步通常直接成功
                if (File.Exists(exe)) File.Delete(exe);

                if (deleteData) Directory.Delete(dir, recursive: true);
                else
                {
                    // 保留 data，但要把其它子目录扫干净。
                    // 以前只删根目录下的文件：logs\ 里是卸载过程自己写的 setup.log，
                    // 那时还被占用删不掉，进程退出后就成了永久残留 ——
                    // 用户看到"卸载完了目录还在"，只能手动删。
                    foreach (var d in Directory.GetDirectories(dir))
                    {
                        if (Path.GetFileName(d).Equals("data", StringComparison.OrdinalIgnoreCase)) continue;
                        try { Directory.Delete(d, recursive: true); } catch { /* 真被占用就留着 */ }
                    }
                }
                Log.Write($"finish delete ok (deleteData={deleteData})");
                CleanTempCopies();
                return true;
            }
            catch (Exception ex)
            {
                Log.Write($"finish delete retry {attempt}: {ex.Message}");
                Thread.Sleep(250);
            }
        }
        Log.Write("finish delete gave up");
        return false;
    }

    /// <summary>清掉以前遗留的临时副本（当前的自己删不掉，留着无害）。</summary>
    private static void CleanTempCopies()
    {
        try
        {
            var self = SetupMain.SelfPath();
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), "cd-uninst-*.exe"))
            {
                if (string.Equals(f, self, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    /// <summary>清空安装目录内容（可保留 data）。</summary>
    public static void PurgeDirectory(string dir, bool deleteData, bool keepSelf = false)
    {
        if (!Directory.Exists(dir)) return;
        var selfPath = SetupMain.SelfPath()?.ToLowerInvariant();
        foreach (var f in Directory.GetFiles(dir))
        {
            if (keepSelf && selfPath != null && f.ToLowerInvariant() == selfPath) continue;
            if (keepSelf && Path.GetFileName(f).Equals("uninstall.exe", StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(f); } catch { }
                continue;
            }
            try { File.Delete(f); } catch { }
        }
        foreach (var d in Directory.GetDirectories(dir))
        {
            var name = Path.GetFileName(d);
            if (!deleteData && name.Equals("data", StringComparison.OrdinalIgnoreCase))
            {
                // 保留配置与统计，只清运行时缓存和进程残留
                SafeDeleteDir(Path.Combine(d, "runtime"));
                SafeDeleteDir(Path.Combine(d, "webview"));
                SafeDelete(Path.Combine(d, "server.pid"));
                continue;
            }
            SafeDeleteDir(d);
        }
    }

    // ---------------------------------------------------------------- 辅助

    /// <summary>
    /// 检测 WebView2 运行时（Evergreen）。没有它主程序只能退到浏览器模式，
    /// 所以安装完要主动告诉用户一声，并给出自带安装器的入口。
    /// <summary>
    /// 检测 .NET Framework 4.8。主程序是 net48 编译的，4.8 缺失会直接打不开。
    /// Win10 1903+ 与 Win11 都自带；Release 键值 528040 及以上即 4.8。
    /// </summary>
    public static bool HasNet48()
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
            {
                var v = k == null ? null : k.GetValue("Release");
                if (v is int i) return i >= 528040;
                if (v is long l) return l >= 528040;
            }
        }
        catch { }
        // 读不到就当有，别因为一次注册表读取失败把正常安装拦下来
        return true;
    }

    /// <summary>
    /// 找出安装目录里正在运行的进程。除了主程序，还要算上它拉起的后端
    /// runtime\node.exe 和内核 core\verge-mihomo.exe —— 主程序被强杀时后端会变孤儿并锁住文件，
    /// 卸载就会卡在 "对路径 node.exe 的访问被拒绝"。
    /// node 是极常见的进程名，**必须按可执行文件路径过滤**，否则会误杀用户其他项目。
    /// </summary>
    public static List<Process> RunningApps(string dir)
    {
        var list = new List<Process>();
        string full = null;
        try { full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar); } catch { }

        // ClashDeck 名字足够唯一，读不到路径时按名字信；node / verge-mihomo 只认路径
        var targets = new[]
        {
            new { Name = "ClashDeck", TrustName = true },
            new { Name = "node", TrustName = false },
            new { Name = "verge-mihomo", TrustName = false },
        };

        foreach (var t in targets)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(t.Name); }
            catch { continue; }

            foreach (var p in procs)
            {
                bool take = false;
                try
                {
                    string path = null;
                    try { path = p.MainModule != null ? p.MainModule.FileName : null; } catch { path = null; }

                    if (string.IsNullOrEmpty(full)) take = t.TrustName;
                    else if (string.IsNullOrEmpty(path)) take = t.TrustName;
                    else take = path.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                }
                catch { take = t.TrustName; }

                if (take) list.Add(p);
                else { try { p.Dispose(); } catch { } }
            }
        }
        return list;
    }

    private static void SafeDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { }
    }

    /// <summary>
    /// 这个产品改过名（Clash Deck → 折跃）。改名前装的快捷方式留在开始菜单与桌面上，
    /// 指向的旧安装目录早被换掉了，点开只会报错或什么都不发生。
    /// 装新版时顺手清掉，避免「一个产品两个图标、其中一个还是死的」。
    /// </summary>
    private static readonly string[] LegacyAppNames = { "Clash Deck" };

    private static void CleanupLegacyShortcuts(string programs, string desktop)
    {
        foreach (var oldName in LegacyAppNames)
        {
            if (string.Equals(oldName, SetupMain.AppName, StringComparison.OrdinalIgnoreCase)) continue;

            var oldMenu = Path.Combine(programs, oldName);
            if (Directory.Exists(oldMenu))
            {
                SafeDeleteDir(oldMenu);
                Log.Write("removed legacy start menu: " + oldMenu);
            }

            var oldLnk = Path.Combine(desktop, oldName + ".lnk");
            if (File.Exists(oldLnk))
            {
                SafeDelete(oldLnk);
                Log.Write("removed legacy desktop shortcut: " + oldLnk);
            }
        }
    }

    private static void SafeDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    public static long DirSize(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
        catch { return 0; }
    }
}

internal static class Shortcut
{
    /// <summary>用 WScript.Shell 建 .lnk；COM 不可用时退化成 .url（仍能双击打开）。</summary>
    public static bool Create(string lnkPath, string target, string workDir, string desc, string icon)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t != null)
            {
                dynamic shell = Activator.CreateInstance(t);
                dynamic lnk = shell.CreateShortcut(lnkPath);
                lnk.TargetPath = target;
                lnk.WorkingDirectory = workDir;
                lnk.Description = desc;
                lnk.IconLocation = icon + ",0";
                lnk.WindowStyle = 1;
                lnk.Save();
                Log.Write("shortcut ok: " + lnkPath);
                return true;
            }
            Log.Write("WScript.Shell 不可用，改用 .url");
        }
        catch (Exception ex)
        {
            Log.Write("shortcut COM failed: " + ex.Message);
        }

        try
        {
            var urlPath = Path.ChangeExtension(lnkPath, ".url");
            var sb = new StringBuilder();
            sb.AppendLine("[InternetShortcut]");
            sb.AppendLine("URL=fil./" + target.Replace('\\', '/'));
            sb.AppendLine("IconFile=" + icon);
            sb.AppendLine("IconIndex=0");
            File.WriteAllText(urlPath, sb.ToString(), Encoding.UTF8);
            Log.Write("shortcut fallback: " + urlPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("shortcut fatal: " + ex.Message);
            return false;
        }
    }
}
