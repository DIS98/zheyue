using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClashDeck.Core
{
    public static class Paths
    {
        public static string Root { get; } = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        public static string NodeExe => Path.Combine(Root, "runtime", "node.exe");
        public static string ServerJs => Path.Combine(Root, "server.js");
        public static string SystemProxyTool => Path.Combine(Root, "tools", "sysproxy.exe");
        public static string WebView2Setup => Path.Combine(Root, "tools", "MicrosoftEdgeWebview2Setup.exe");

        public static string LogFile => Path.Combine(Root, "logs", "app.log");

        public static void WriteLog(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(Root, "logs"));
                lock (typeof(Paths))
                {
                    // 轮转：超过 2MB 就把当前日志改名成 app.log.1（旧的 .1 直接覆盖）。
                    // 不加这一步，长期挂着的实例会把日志写成几百 MB。
                    try
                    {
                        var fi = new FileInfo(LogFile);
                        if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                        {
                            var old = LogFile + ".1";
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(LogFile, old);
                        }
                    }
                    catch { /* 轮转失败就继续追加 */ }
                    File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss} {msg}{Environment.NewLine}", new UTF8Encoding(true));
                }
            }
            catch { /* 日志失败不影响主流程 */ }
        }
    }

    /// <summary>
    /// 后端启动失败的机器可读原因。后端在退出前打一行 <c>[[CD_FATAL]]{json}</c>，
    /// 主程序解析它，而不是去猜中文文案 —— 文案改一个字界面就失灵的耦合不能留。
    /// </summary>
    public sealed class FatalInfo
    {
        public string Code = "";
        public int Port;
        public List<int> Free = new List<int>();
        public string ConfigBackup = "";
        public string ConfigReason = "";

        public const string Marker = "[[CD_FATAL]]";
    }

    /// <summary>
    /// 后端（Node 面板进程）的启停管理。已有实例在跑就直接接管，不重复起。
    /// 从 WinForms 版原样移植，只把 .NET Core 专有 API 换掉。
    /// </summary>
    public sealed class Backend : IDisposable
    {
        private Process _proc;

        public bool WeOwnIt { get; private set; }
        private readonly List<string> _errTail = new List<string>();
        public bool Ready { get; private set; }

        /// <summary>最近一次启动失败的结构化原因；成功则为 null。</summary>
        public FatalInfo Fatal { get; private set; }
        public int Pid => _proc != null && !_proc.HasExited ? _proc.Id : 0;

        public Task<bool> ProbeAsync(int timeoutMs = 800) => Api.PingAsync(timeoutMs);

        public async Task<string> StartAsync()
        {
            Fatal = null;
            await AlignPortWithConfigAsync().ConfigureAwait(false);
            if (await ProbeAsync().ConfigureAwait(false))
            {
                Ready = true;
                WeOwnIt = false;
                Paths.WriteLog("attach existing backend");
                return "已连接到正在运行的后端";
            }

            if (!File.Exists(Paths.NodeExe)) return "找不到 runtime\\node.exe，后端无法启动";
            if (!File.Exists(Paths.ServerJs)) return "找不到 server.js，安装不完整";

            // 参数走命令行：EnvironmentVariables 在 net48 上遇到 HTTPS_PROXY/https_proxy
            // 这种大小写重名会抛 "已添加项"，而且用户环境不一定干净。
            var args = "server.js --no-browser --parent-pid " + Process.GetCurrentProcess().Id;
            var psi = new ProcessStartInfo(Paths.NodeExe, args)
            {
                WorkingDirectory = Paths.Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Paths.WriteLog("node " + e.Data); };
            _proc.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                Paths.WriteLog("node! " + e.Data);
                // 后端起不来时，唯一能说清原因的就是 stderr 里这几行。
                // 只写进日志等于让用户去翻文件，所以留一份尾部带回给界面。
                lock (_errTail)
                {
                    _errTail.Add(e.Data.Trim());
                    if (_errTail.Count > 6) _errTail.RemoveAt(0);
                }
            };
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            WeOwnIt = true;
            Paths.WriteLog("backend spawned pid=" + _proc.Id);

            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(250).ConfigureAwait(false);
                if (await ProbeAsync(400).ConfigureAwait(false)) { Ready = true; return "后端已就绪"; }
                if (_proc.HasExited)
                {
                    Fatal = ParseFatal();
                    if (Fatal != null && Fatal.Code == "EADDRINUSE")
                        return "面板端口 " + Fatal.Port + " 已被占用";

                    string tail = "";
                    lock (_errTail)
                    {
                        if (_errTail.Count > 0)
                        {
                            var sb = new StringBuilder();
                            var from = Math.Max(0, _errTail.Count - 3);
                            for (int k = from; k < _errTail.Count; k++)
                            {
                                if (sb.Length > 0) sb.Append(" / ");
                                sb.Append(_errTail[k]);
                            }
                            tail = sb.ToString();
                        }
                    }
                    return string.IsNullOrEmpty(tail)
                        ? "后端启动即退出，详见 logs\\app.log"
                        : "后端启动失败：" + tail;
                }
            }
            return "后端 10 秒内未响应，详见 logs\\app.log";
        }

        /// <summary>
        /// 把界面的后端地址对齐到 config.json 里的 webPort。
        ///
        /// 界面默认连 8787，但面板端口是可以在「配置」里改的。改完重启软件，
        /// 界面还在探 8787、后端却监听新端口 —— 表现是"后端 10 秒内未响应"，
        /// 而后端其实活得好好的（日志里能看到 Clash Deck: http://127.0.0.1:新端口）。
        /// 这个函数只在"当前端口连不上"时才动手，所以指定端口跑多实例的用法不受影响。
        /// </summary>
        private async Task AlignPortWithConfigAsync()
        {
            // A11：命令行钉死了端口就绝不动手。原来这里只看"当前端口连不连得上"，
            // 于是 `ClashDeck.exe --api-port <没人监听的端口>` 会被静默改写成
            // config.json 的 webPort —— 也就是用户正在用的那个实例。
            if (Api.PortPinned)
            {
                Paths.WriteLog("align skipped: --api-port pinned " + Api.BaseUrl);
                return;
            }
            if (await ProbeAsync(400).ConfigureAwait(false)) return;
            try
            {
                var cfg = Path.Combine(Paths.Root, "data", "config.json");
                if (!File.Exists(cfg)) return;   // 没有配置 = 后端会用默认端口，与界面一致
                var j = new JObj(Json.Parse(File.ReadAllText(cfg, Encoding.UTF8)));
                int port = j.I("webPort");
                if (port < 1 || port > 65535) return;
                var want = "http://127.0.0.1:" + port;
                if (string.Equals(want, Api.BaseUrl, StringComparison.OrdinalIgnoreCase)) return;
                Paths.WriteLog("api base aligned to config: " + Api.BaseUrl + " -> " + want);
                Api.BaseUrl = want;
            }
            catch (Exception ex) { Paths.WriteLog("align port failed: " + ex.Message); }
        }

        /// <summary>从 stderr 尾部找 [[CD_FATAL]] 那一行并解析。找不到返回 null。</summary>
        private FatalInfo ParseFatal()
        {
            string all;
            lock (_errTail) all = string.Join("\n", _errTail);
            int i = all.IndexOf(FatalInfo.Marker, StringComparison.Ordinal);
            if (i < 0) return null;
            string rest = all.Substring(i + FatalInfo.Marker.Length);
            int nl = rest.IndexOf('\n');
            if (nl >= 0) rest = rest.Substring(0, nl);
            try
            {
                var j = new JObj(Json.Parse(rest.Trim()));
                var info = new FatalInfo
                {
                    Code = j.S("code"),
                    Port = j.I("port"),
                    ConfigBackup = j.S("configBackup"),
                    ConfigReason = j.S("configReason"),
                };
                var arr = j["free"].Raw as System.Collections.IEnumerable;
                if (arr != null)
                {
                    foreach (var it in arr)
                    {
                        int p = Convert.ToInt32(it);
                        if (p > 0 && p <= 65535) info.Free.Add(p);
                    }
                }
                return info;
            }
            catch { return null; }
        }

        /// <summary>
        /// 让后端把面板端口写进 config.json（node server.js --set-web-port n）。
        /// 端口被占时后端根本没在跑，API 是断的，只能走这个一次性命令。
        /// 返回空串表示成功，否则是给用户看的原因。
        /// </summary>
        public async Task<string> SetWebPortAsync(int port)
        {
            if (!File.Exists(Paths.NodeExe)) return "找不到 runtime\\node.exe，无法修改端口";
            if (!File.Exists(Paths.ServerJs)) return "找不到 server.js，安装不完整";

            var psi = new ProcessStartInfo(Paths.NodeExe, "server.js --set-web-port " + port)
            {
                WorkingDirectory = Paths.Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using (var p = Process.Start(psi))
            {
                // 先读流再等退出，反过来会在大输出时死锁。
                var errTask = p.StandardError.ReadToEndAsync();
                var outTask = p.StandardOutput.ReadToEndAsync();
                p.WaitForExit(10000);
                string err = (await errTask.ConfigureAwait(false)) ?? "";
                await outTask.ConfigureAwait(false);
                Paths.WriteLog("set-web-port " + port + " -> " + err.Replace("\r", " ").Replace("\n", " ").Trim());

                int i = err.IndexOf("[[CD_PORT_SET]]", StringComparison.Ordinal);
                if (i < 0) return "修改端口失败：命令没有回执，详见 logs\\app.log";
                try
                {
                    var j = new JObj(Json.Parse(err.Substring(i + "[[CD_PORT_SET]]".Length).Trim()));
                    if (!j.B("ok")) return j.S("msg") ?? "修改端口失败";
                    return "";
                }
                catch { return "修改端口失败：回执无法解析"; }
            }
        }

        /// <summary>
        /// 让后端把内核停掉（尽力而为）。
        ///
        /// 超时 2.5s → 10s：服务端 stopCoreInner 的设计最坏值是
        /// 「等内核 6000ms + waitPortFree 3000ms + 两次 sysproxy.exe 进程启动」，
        /// 实测 sysproxy.exe 冷启动 2187ms —— 2.5s 必然先超时，而超时后
        /// Dispose() 的 taskkill /T /F 会连内核一起强杀，服务端"内核已停 → 关代理"
        /// 那一步永远执行不到，于是系统代理留在指向死端口的状态。
        /// </summary>
        public async Task StopCoreAsync()
        {
            try
            {
                using (var cts = new CancellationTokenSource(10000))
                {
                    var j = await Api.Post("/api/core/stop", null, 10000).ConfigureAwait(false);
                    Paths.WriteLog("core stop -> " + (j.Exists ? j.S("msg") : "no response"));
                }
            }
            catch (Exception ex) { Paths.WriteLog("core stop failed: " + ex.Message); }
        }

        /// <summary>
        /// 收尸：只杀「确认是本安装目录的 node」。
        ///
        /// 四重校验缺一不可：① server.pid 能解析出正整数；② 该 pid 还活着；
        /// ③ 它的 MainModule 路径**等于**本目录的 runtime\node.exe；④ 读不到路径就放弃。
        /// 为什么必须比路径而不是比进程名：用户把安装目录拷了一份（D 盘 E 盘各一份）时，
        /// A 目录的 server.pid 可能指向 B 目录的 node；而强杀残留的旧 pid 还可能
        /// 已被系统复用成任意进程 —— 只比名字就会杀错人。
        /// </summary>
        public static bool TryKillZombie()
        {
            try
            {
                var pidFile = Path.Combine(Paths.Root, "data", "server.pid");
                if (!File.Exists(pidFile)) { Paths.WriteLog("zombie: no server.pid"); return false; }
                if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) || pid <= 0)
                {
                    Paths.WriteLog("zombie: server.pid 内容不是一个正整数，放弃收尸");
                    return false;
                }

                Process p;
                try { p = Process.GetProcessById(pid); }   // pid 不存在时**抛异常**，不返回 null
                catch (Exception ex)
                {
                    Paths.WriteLog("zombie: GetProcessById(" + pid + ") " + ex.GetType().Name);
                    return false;
                }

                using (p)
                {
                    string exe = null;
                    // 跨位数 / 提权差异会抛 Win32Exception：读不到就当作"不是我们的"，绝不杀
                    try { exe = p.MainModule == null ? null : p.MainModule.FileName; }
                    catch { exe = null; }

                    if (string.IsNullOrEmpty(exe) ||
                        !string.Equals(exe, Paths.NodeExe, StringComparison.OrdinalIgnoreCase))
                    {
                        Paths.WriteLog("zombie: pid " + pid + " 不是本安装目录的 node（" + (exe ?? "读不到路径") + "），不杀");
                        return false;
                    }

                    p.Kill();
                    p.WaitForExit(4000);
                    Paths.WriteLog("zombie: killed stale backend pid " + pid);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Paths.WriteLog("zombie kill failed: " + ex.Message);
                return false;
            }
        }

        public void Dispose()
        {
            if (_proc == null) return;
            try
            {
                if (!_proc.HasExited)
                {
                    // net48 没有 Kill(entireProcessTree)，用 taskkill /T 连子进程（内核）一起收
                    try
                    {
                        var kill = new ProcessStartInfo("taskkill", $"/PID {_proc.Id} /T /F")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                        };
                        using (var p = Process.Start(kill)) p?.WaitForExit(3000);
                    }
                    catch { try { _proc.Kill(); } catch { } }
                    _proc.WaitForExit(3000);
                    Paths.WriteLog("backend killed");
                }
            }
            catch (Exception ex) { Paths.WriteLog("kill failed: " + ex.Message); }
            try { _proc.Dispose(); } catch { }
            _proc = null;
        }
    }
}
