using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ClashDeck.Core
{
    /// <summary>
    /// 系统代理的「壳侧」直写实现。
    ///
    /// 为什么壳也要会写注册表：
    ///  - 用户点"退出"时后端可能已经死了，而"关代理"不能挂在被关的那个进程上；
    ///  - 工具缺失（tools\sysproxy.exe 不在）时，后端与壳的 spawn 都会失败，只有直写还能收场；
    ///  - 退出路径要的是"持久化干净"，不需要 InternetSetOption 的即时广播
    ///    （交互式开关仍走后端 + sysproxy.exe，那里必须有广播，这里刻意不做 SendMessageTimeout）。
    ///
    /// 三条铁律（写在这里，改动前先读）：
    ///  1. **只动确认是我们的东西**。不是我们的，退出路径一律不碰；
    ///  2. **删之前必须有证据**。无备份时无法区分"我们写的"和"用户自建同端口"，所以那种情况只置 0、不删值；
    ///  3. **宣布成功之前必须回读**。"我以为写进去了"是本轮所有假话的共同来源。
    /// </summary>
    public static class SysProxy
    {
        // ---------------------------------------------------------------- 日志标记
        // 6 条标记 + via/which 维度（ui 侧验收集按这个 grep）。
        // 规则：**标记只表示事件类别，细分维度一律 key=value**；标记不可改，措辞可改。
        // toast 判定：存在 ⟺ via=click 且 标记 ≠ [B-foreign-cancel]。
        public const string MRestore = "[B-restore]";            // which=B1|B2
        public const string MLegacy = "[B-legacy]";
        public const string MForeign = "[B-foreign]";
        public const string MForeignCancel = "[B-foreign-cancel]";
        public const string MOffFail = "[B-off-fail]";
        public const string MRestoreFail = "[B-restore-fail]";   // 必含「备份未删除」

        /// <summary>日志里的维度值：本次动作是"用户主动点的"还是"退出/关机触发的"。</summary>
        private static string Via(Caller c) => c == Caller.Click ? "click" : "exit";

        private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        private const int INTERNET_OPTION_REFRESH = 37;
        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferSize);

        /// <summary>
        /// 与 src/SysProxy/Program.cs 的 DefaultBypass **必须逐字一致**。
        /// 它是"这串是我们写的"的指纹：Enable() 只在原 ProxyOverride 为空时才写它，
        /// 所以精确相等即可证明归属。**不要改成"包含某一项"的宽松匹配** ——
        /// 宽松匹配等于没有指纹，失败方向会从"漏关"变成"误关别人的代理"。
        /// 改这个串时，Program.cs 那边要同时改。
        /// </summary>
        public const string DefaultBypass =
            "<local>;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;"
            + "172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;localhost;::1";

        /// <summary>调用者。主动点击与被动退出对"不是我们的代理"处理完全不同。</summary>
        public enum Caller
        {
            /// <summary>用户点了开关 —— 他明确要求关代理，所以"不是我们的"也只置 0。</summary>
            Click,
            /// <summary>退出 / 关机 / 超时 —— 他没要求关别人的代理，不是我们的一律不碰。</summary>
            Exit,
        }

        // ---------------------------------------------------------------- 注册表读写

        public static int ReadEnable()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegPath, false))
                {
                    var v = k?.GetValue("ProxyEnable");
                    if (v is int i) return i;
                    if (v != null && int.TryParse(v.ToString(), out var p)) return p;
                    return 0;
                }
            }
            catch (Exception ex) { Paths.WriteLog("sys 读 ProxyEnable 失败: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        public static string ReadServer()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegPath, false))
                    return (k?.GetValue("ProxyServer") as string) ?? "";
            }
            catch (Exception ex) { Paths.WriteLog("sys 读 ProxyServer 失败: " + ex.GetType().Name + " " + ex.Message); return ""; }
        }

        public static string ReadOverride()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegPath, false))
                    return (k?.GetValue("ProxyOverride") as string) ?? "";
            }
            catch (Exception ex) { Paths.WriteLog("sys 读 ProxyOverride 失败: " + ex.GetType().Name + " " + ex.Message); return ""; }
        }

        /// <summary>写一个字符串值。返回是否成功（不抛）。</summary>
        private static bool TrySetString(string name, string value)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegPath, true))
                {
                    if (k == null) return false;
                    k.SetValue(name, value ?? "", RegistryValueKind.String);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Paths.WriteLog("sys 写 " + name + " 失败: " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        private static bool TrySetEnable(int value)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegPath, true))
                {
                    if (k == null) return false;
                    k.SetValue("ProxyEnable", value, RegistryValueKind.DWord);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Paths.WriteLog("sys 写 ProxyEnable 失败: " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        private static bool TryDelete(string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegPath, true))
                {
                    if (k == null) return false;
                    k.DeleteValue(name, false);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Paths.WriteLog("sys 删 " + name + " 失败: " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 通知 WinINet「设置变了」。**只这两句，不做 SendMessageTimeout** ——
        /// 广播那步要等每个顶层窗口泵消息（sysproxy.exe 里给了 1000ms 超时），
        /// 退出路径上等它就是又造一个超时。
        /// 这两句是同步、无超时、微秒级；失败也不重试（重试只会拖长关停）。
        /// </summary>
        public static void NotifyWinInet()
        {
            try
            {
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
            catch (Exception ex)
            {
                // 关机途中 wininet 可能已在卸载；异常绝不能漏进关停路径
                Paths.WriteLog("sys InternetSetOption 失败: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 回环判定

        /// <summary>
        /// <paramref name="server"/> 是不是全都指向本机回环。
        ///
        /// 用途：决定"用户点关闭时要不要弹一次确认"。
        ///  - 回环（另一个本地代理工具崩了、注册表还指着它的死端口）→ **不确认**，
        ///    那是用户断网自救的路径，弹窗等于在救命时刻加门槛；
        ///  - 远程（公司代理）→ **确认**，关掉它会真的断掉别人的配置。
        ///
        /// 默认从严：**任何一段不是回环就整体按远程**；解析不出也按远程。
        /// 少问一次的代价是用户公司代理被静默关掉，多问一次的代价只是一次点击。
        /// </summary>
        public static bool IsLoopbackAll(string server)
        {
            if (string.IsNullOrWhiteSpace(server)) return false;      // 空 → 远程（从严）
            if (server.IndexOf('*') >= 0) return false;               // 含通配符 → 远程（ProxyServer 不该有 *）
            foreach (var rawSeg in server.Split(';'))
            {
                var seg = rawSeg.Trim();
                if (seg.Length == 0) continue;
                var host = HostOf(seg);
                if (host.Length == 0) return false;                   // 解析不出 → 远程（从严）
                if (!IsLoopbackHost(host)) return false;               // 任一段远程 → 整体远程
            }
            return true;
        }

        /// <summary>取 host：先剥 scheme 前缀，再处理 IPv6 的方括号，再按"最后一个冒号"切端口。</summary>
        private static string HostOf(string seg)
        {
            // http= / https= / socks= / socks5= / ftp=
            var eq = seg.IndexOf('=');
            if (eq > 0) seg = seg.Substring(eq + 1).Trim();

            if (seg.StartsWith("["))
            {
                // IPv6 字面量：[::1]:8080 —— 直接 Split(':') 会被切成 ['[','','1]','8080']
                var end = seg.IndexOf(']');
                if (end <= 0) return "";
                return seg.Substring(0, end + 1).ToLowerInvariant();   // 保留方括号，便于统一比较
            }

            var colon = seg.LastIndexOf(':');
            if (colon > 0) seg = seg.Substring(0, colon);
            return seg.Trim().ToLowerInvariant();
        }

        private static bool IsLoopbackHost(string host)
        {
            if (host == "localhost") return true;
            if (host == "[::1]" || host == "::1") return true;
            // 127.0.0.0/8：首段是 127
            var dot = host.IndexOf('.');
            var first = dot > 0 ? host.Substring(0, dot) : host;
            return first == "127";
        }

        /// <summary>给日志用的脱敏：ProxyServer 某些写法会带 user:pass@，凭据不该长期留在日志里。</summary>
        private static string Mask(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var at = s.IndexOf('@');
            if (at > 0) s = "<凭据已隐藏>" + s.Substring(at);          // 保留 @host 便于排障
            if (s.Length > 120) s = s.Substring(0, 120) + "…";
            return s;
        }

        // ---------------------------------------------------------------- 备份文件

        /// <summary>
        /// 备份落在 <c>data\proxy-backup.txt</c>。
        /// **不在 tools\ 下**：安装器卸载时只保留 data\，放 tools\ 就是"卸载即丢"，
        /// 而这里的备份是用户原始代理配置的唯一副本。
        /// </summary>
        public static string BackupPath => Path.Combine(Paths.Root, "data", "proxy-backup.txt");

        private sealed class Backup
        {
            public bool Exists;
            public long At;
            public int OrigEnable;
            public string OrigServer = "";
            public string OrigOverride = "";
            public string WrittenServer = "";
            public string WrittenOverride = "";
            public bool HostOk;
        }

        /// <summary>
        /// 读备份。**先用 File.Exists 挡**（路径上是目录时会抛 UnauthorizedAccessException，
        /// 不是 FileNotFoundException，同一条 catch 兜不住），外层再兜异常类型进日志。
        /// 回读与真读共用这一段，避免"写时宽松、读时严格"的错位。
        /// </summary>
        private static Backup ReadBackup()
        {
            var b = new Backup();
            var p = BackupPath;
            try
            {
                if (!File.Exists(p)) return b;                        // 注意：File.Exists 对目录返回 false，这是有意的
                var text = File.ReadAllText(p, Encoding.UTF8);
                var j = new JObj(Json.Parse(text));
                if (!j.Exists) return b;
                b.Exists = true;
                b.At = j.L("at");
                var o = j["original"];
                b.OrigEnable = o.I("enable");
                b.OrigServer = o.S("server");
                b.OrigOverride = o.S("override");
                var w = j["written"];
                b.WrittenServer = w.S("server");
                b.WrittenOverride = w.S("override");
                var host = w.Has("host") ? w.S("host") : w.S("host");
                // host 不匹配 → 当"没有这个键"（config 被拷到别的机器上时不认别人写的串）
                b.HostOk = host.Length == 0
                           || string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
                return b;
            }
            catch (Exception ex)
            {
                Paths.WriteLog("sys 读备份失败: " + ex.GetType().Name + " " + ex.Message);
                return new Backup();
            }
        }

        // ---------------------------------------------------------------- 归属判据

        private enum Owner { OursByBackup, OursByFingerprint, NotOurs }

        /// <summary>
        /// 当前注册表里的代理是不是我们写的。三段判据（顺序有意义）：
        ///  ① 备份里有 written.server 且 host 是本机 且 reg.ProxyServer 等于它 → 是我们的（覆盖端口漂移）
        ///  ② 没有备份（或不认这台机）且 reg.ProxyServer == 127.0.0.1:&lt;mixedPort&gt;
        ///     且 reg.ProxyOverride 精确等于 DefaultBypass 指纹 → 是我们的（老版本升级路径）
        ///  ③ 其余 → 不是我们的
        ///
        /// ② 的第二信号（指纹）是必须的：7897 既是我们的默认端口、也是 Clash Verge 的默认端口，
        /// 只比端口会把别人的代理关掉。
        /// </summary>
        private static Owner WhoOwns(string regServer, string regOverride, Backup bk)
        {
            if (bk.Exists && bk.HostOk && bk.WrittenServer.Length > 0
                && string.Equals(regServer, bk.WrittenServer, StringComparison.OrdinalIgnoreCase))
                return Owner.OursByBackup;

            if (!bk.Exists || !bk.HostOk)
            {
                var want = "127.0.0.1:" + CurrentMixedPort();
                if (string.Equals(regServer, want, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(regOverride, DefaultBypass, StringComparison.Ordinal))
                    return Owner.OursByFingerprint;
            }
            return Owner.NotOurs;
        }

        /// <summary>从 config.json 读 mixedPort（壳在 AlignPortWithConfigAsync 里已有同样的读取方式）。</summary>
        private static int CurrentMixedPort()
        {
            try
            {
                var cfg = Path.Combine(Paths.Root, "data", "config.json");
                if (!File.Exists(cfg)) return 7897;
                var j = new JObj(Json.Parse(File.ReadAllText(cfg, Encoding.UTF8)));
                var p = j.I("mixedPort");
                return p > 0 && p <= 65535 ? p : 7897;
            }
            catch { return 7897; }
        }

        // ---------------------------------------------------------------- 主入口

        /// <summary>
        /// 关掉系统代理。返回是否"确认已关闭"（回读为 0）。失败不代表什么都没做，只代表没确认成功。
        /// </summary>
        public static bool DirectProxyOff(Caller caller)
        {
            var regServer = ReadServer();
            var regOverride = ReadOverride();
            var bk = ReadBackup();
            var owner = WhoOwns(regServer, regOverride, bk);

            // ---- 不是我们的
            if (owner == Owner.NotOurs)
            {
                if (caller == Caller.Exit)
                {
                    // 用户没要求关别人的代理
                    Paths.WriteLog(MForeign + " via=exit sys 系统代理非本应用写入（当前 " + Mask(regServer) + "），退出时不处理");
                    return ReadEnable() == 0;
                }
                // 用户明确点了关闭：只关开关，**绝不删改**别人的地址与例外列表
                if (!TrySetEnable(0))
                {
                    Paths.WriteLog(MOffFail + " via=" + Via(caller) + " sys 关闭系统代理失败（写 ProxyEnable 报错），代理仍指向 " + Mask(regServer));
                    return false;
                }
                NotifyWinInet();
                if (ReadEnable() != 0)
                {
                    Paths.WriteLog(MOffFail + " via=" + Via(caller) + " sys 关闭系统代理失败（回读 ProxyEnable 仍非 0），代理仍指向 " + Mask(regServer));
                    return false;
                }
                Paths.WriteLog(MForeign + " via=click sys 系统代理非本应用写入（当前 " + Mask(regServer) + "），按用户请求仅关闭开关，未改动地址与例外列表");
                return true;
            }

            // ---- 是我们的：先关开关，再按分支还原/保留
            var enableOk = TrySetEnable(0);
            if (!enableOk || ReadEnable() != 0)
            {
                Paths.WriteLog(MOffFail + " via=" + Via(caller) + " sys 关闭系统代理失败（ProxyEnable 回读仍非 0），代理仍指向 " + Mask(regServer));
                return false;
            }

            if (!bk.Exists || !bk.HostOk)
            {
                // B④：无备份（老版本存量 / 或本版本写备份失败）。没有证据，就只关开关、不删值。
                NotifyWinInet();
                Paths.WriteLog(MLegacy + " via=" + Via(caller) + " sys 无备份的存量配置：仅关闭代理，未改动地址与例外列表");
                return true;
            }

            // 有备份：逐字段判断"有没有资格覆盖"
            var overrideUntouched = string.Equals(regOverride, bk.WrittenOverride, StringComparison.Ordinal);
            var restoreEnable = bk.OrigEnable;
            var restoreServer = bk.OrigServer ?? "";
            var restoreOverride = bk.OrigOverride ?? "";

            // 还原 enable（我们现在要关它，所以按备份的原始值）
            if (!TrySetEnable(restoreEnable))
            {
                Paths.WriteLog(MRestoreFail + " via=" + Via(caller) + " which=B1 field=ProxyEnable reason=写入报错，备份未删除");
                NotifyWinInet();
                return false;
            }
            var enableRead = ReadEnable();
            if (enableRead != restoreEnable)
            {
                Paths.WriteLog(MRestoreFail + " via=" + Via(caller) + " which=B1 field=ProxyEnable reason=回读不一致（期望 " + restoreEnable + " 实际 " + enableRead + "），备份未删除");
                NotifyWinInet();
                return false;
            }

            // 还原 server（空串表示"原本没配过"，写空串而不是删键：B8 的删除纪律只允许 exe 在还原缺省态时删）
            if (!TrySetString("ProxyServer", restoreServer))
            {
                Paths.WriteLog(MRestoreFail + " via=" + Via(caller) + " which=B1 field=ProxyServer reason=写入报错，备份未删除");
                NotifyWinInet();
                return false;
            }
            if (!string.Equals(ReadServer(), restoreServer, StringComparison.Ordinal))
            {
                Paths.WriteLog(MRestoreFail + " via=" + Via(caller) + " which=B1 field=ProxyServer reason=回读不一致，备份未删除");
                NotifyWinInet();
                return false;
            }

            if (overrideUntouched)
            {
                if (!TrySetString("ProxyOverride", restoreOverride))
                {
                    Paths.WriteLog(MRestoreFail + " via=" + Via(caller) + " which=B1 field=ProxyOverride reason=写入报错，备份未删除");
                    NotifyWinInet();
                    return false;
                }
                if (!string.Equals(ReadOverride(), restoreOverride, StringComparison.Ordinal))
                {
                    Paths.WriteLog(MRestoreFail + " via=" + Via(caller) + " which=B1 field=ProxyOverride reason=回读不一致，备份未删除");
                    NotifyWinInet();
                    return false;
                }
                NotifyWinInet();
                Paths.WriteLog(MRestore + " via=" + Via(caller) + " which=B1 sys 已按备份还原：ProxyEnable=" + restoreEnable
                               + "、ProxyServer=" + Mask(restoreServer)
                               + "、ProxyOverride=" + Mask(restoreOverride) + "（回读确认）");
                TryDeleteBackup();
                return true;
            }

            // B②：override 被用户改过 → 只还原 enable+server，保留用户的例外列表
            NotifyWinInet();
            Paths.WriteLog(MRestore + " via=" + Via(caller) + " which=B2 sys 已还原 enable+server，override 保留用户改动（当前 " + Mask(regOverride)
                           + "，我们写入 " + Mask(bk.WrittenOverride) + "）（回读确认）");
            TryDeleteBackup();
            return true;
        }

        /// <summary>备份只在"成功还原"后删除；任何失败路径都不删，否则数据真没了。</summary>
        private static void TryDeleteBackup()
        {
            try { if (File.Exists(BackupPath)) File.Delete(BackupPath); }
            catch (Exception ex) { Paths.WriteLog("sys 删备份失败: " + ex.GetType().Name + " " + ex.Message); }
        }

        /// <summary>
        /// 失联态点「关闭」用：不带交互确认，只关开关（本地实现，不依赖后端）。
        /// 与 <see cref="DirectProxyOff"/> 走同一段代码，避免出现两套实现。
        /// </summary>
        public static bool OffFromShellNoConfirm()
        {
            return DirectProxyOff(Caller.Click);
        }
    }
}
