using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClashDeck.Core
{
    /// <summary>
    /// 后端 HTTP 接口的唯一入口。
    /// 注意：HttpClientHandler.UseProxy = false 是必须的 —— 系统代理一旦指向本机内核，
    /// 访问 127.0.0.1:8787 就会自己绕自己一圈，表现为“面板连不上”。
    /// </summary>
    public static class Api
    {
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        public static string BaseUrl = "http://127.0.0.1:8787";

        /// <summary>
        /// 命令行用 <c>--api-port</c> 明确指定了后端端口。
        ///
        /// 一旦为 true，<see cref="Backend.AlignPortWithConfigAsync"/> 就**不许**再按
        /// data\config.json 的 webPort 改写 BaseUrl —— 否则"我想连隔离后端"会静默变成
        /// "我连上了用户正在用的那个实例"，验证时改动的是生产环境。
        /// </summary>
        public static bool PortPinned;

        private static async Task<JObj> SendAsync(HttpMethod method, string path, object body, int timeoutMs)
        {
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (var req = new HttpRequestMessage(method, BaseUrl + path))
                {
                    if (body != null)
                    {
                        var json = body is string s ? s : Json.Stringify(body);
                        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    }
                    else if (method != HttpMethod.Get && method != HttpMethod.Head)
                    {
                        // 写请求即使没有参数也带上一个空 JSON 对象。
                        // 以前 body=null 时完全不设 Content，POST 就没有 Content-Type ——
                        // 后端一收紧成"写接口只接受 application/json"，点"启动内核"就直接 403。
                        // 契约应该显式写在请求里，而不是依赖"没 body 所以没类型"这种隐式规则。
                        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    }
                    var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
                    string text;
                    int status;
                    using (resp)
                    {
                        // 读完内容必须释放响应：复用同一个 HttpClient 时，
                        // 不 Dispose 会让连接一直被 MessageHandler 占着，久了就"越用越卡"。
                        status = (int)resp.StatusCode;
                        text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                    var parsed = new JObj(Json.Parse(text));
                    if (!parsed.Exists)
                    {
                        // 后端总会返回 JSON 对象；解析不出来说明端口上不是我们的服务
                        return new JObj(Json.Obj("ok", false, "msg", "接口返回不是 JSON（" + status + "）"));
                    }
                    return parsed;
                }
            }
            catch (OperationCanceledException)
            {
                return new JObj(Json.Obj("ok", false, "msg", "请求超时"));
            }
            catch (Exception ex)
            {
                return new JObj(Json.Obj("ok", false, "msg", ex.Message));
            }
        }

        public static Task<JObj> Get(string path, int timeoutMs = 10000) =>
            SendAsync(HttpMethod.Get, path, null, timeoutMs);

        public static Task<JObj> Post(string path, object body = null, int timeoutMs = 30000) =>
            SendAsync(HttpMethod.Post, path, body, timeoutMs);

        public static Task<JObj> Delete(string path, int timeoutMs = 10000) =>
            SendAsync(HttpMethod.Delete, path, null, timeoutMs);

        public static string Q(string s) => Uri.EscapeDataString(s ?? "");

        /// <summary>本应用后端的身份标识。后端 /api/env 与 /api/snapshot 会带这个字段。</summary>
        public const string AppMarker = "clash-deck";

        /// <summary>
        /// 这份数据是不是「本应用的后端」回的。
        ///
        /// 以前只看 HTTP 状态码 —— 于是"127.0.0.1:8787 上任何一个会回 200 的程序"
        /// 都会被当成自己的后端接管（实测：一个回 <c>{}</c> 的假服务就能让界面
        /// 进入"已连接"状态，然后一路拿空对象，显示成"还没有激活的配置"）。
        ///
        /// 判据分两段，为了兼容后端尚未加 app 字段的那一版：
        ///   ① 有 app 字段 → 必须等于 clash-deck；
        ///   ② 没有 app 字段 → 真快照/真 env 必带的结构（settings/env/core 或 ok+isAdmin 这组）
        /// 注意 SendAsync 失败时返回的是 {ok:false,msg}，它也满足 Exists，
        /// 所以结构判据必须能把它排除掉：它没有 settings/env/core。
        /// </summary>
        public static bool IsOurs(JObj j)
        {
            if (!j.Exists) return false;
            if (j.Has("app")) return string.Equals(j.S("app"), AppMarker, StringComparison.OrdinalIgnoreCase);
            return (j.Has("settings") && j.Has("env") && j.Has("core"))
                   || (j.Has("isAdmin") && j.Has("coreRunning"));
        }

        /// <summary>
        /// 后端是否在线，且「在线的是我们自己的后端」。
        /// 必须看 HTTP 状态码：SendAsync 的失败结果本身也是一个 {ok:false,msg:...} 对象。
        /// </summary>
        public static async Task<bool> PingAsync(int timeoutMs = 900)
        {
            try
            {
                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    var r = await Http.GetAsync(BaseUrl + "/api/env", cts.Token).ConfigureAwait(false);
                    if (!r.IsSuccessStatusCode) return false;
                    var text = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                    // 状态码 200 不足以证明"对面是我们"，必须看载荷
                    return IsOurs(new JObj(Json.Parse(text)));
                }
            }
            catch { return false; }
        }

        public static Task<JObj> Snapshot() => Get("/api/snapshot", 12000);
        public static Task<JObj> Env() => Get("/api/env", 4000);
        /// <summary>
        /// 日志增量。v=2 表示 since 是**单调序号**而不是缓冲下标。
        /// 老协议下 total 到 600 就饱和，客户端把 total 当游标会永久停更 ——
        /// 也就是"超过几百行就不再滚动"的那个现象。参数换了才有新语义。
        /// </summary>
        public static Task<JObj> Logs(long since) => Get("/api/logs?since=" + since + "&v=2", 6000);
        public static Task<JObj> History(int seconds) => Get("/api/history?seconds=" + seconds, 6000);
        public static Task<JObj> Connections() => Get("/api/connections", 8000);
        public static Task<JObj> Rules(string sort) => Get("/api/rules?sort=" + sort, 20000);
        public static Task<JObj> Usage(int days) => Get("/api/usage?days=" + days, 6000);
        public static Task<JObj> DelayHistory(string name) =>
            Get("/api/delay-history" + (string.IsNullOrEmpty(name) ? "" : "?name=" + Q(name)), 6000);
        public static Task<JObj> RuleProviders() => Get("/api/providers/rules", 10000);
        public static Task<JObj> SysProxyStatus() => Get("/api/systemproxy", 8000);
        public static Task<JObj> Backup() => Get("/api/backup", 20000);
        public static Task<JObj> Subscriptions() => Get("/api/subscriptions", 6000);

        // ------------------------------------------------------------ 强类型快照

        public static async Task<Snap> SnapshotModel()
        {
            var j = await Snapshot().ConfigureAwait(false);
            return Snap.From(j);
        }
    }

    public sealed class GroupInfo
    {
        public string Name = "";
        public string Type = "";
        public string Now = "";
        public List<string> All = new List<string>();
        public Dictionary<string, double> Delays = new Dictionary<string, double>(StringComparer.Ordinal);
        public double DelayOfNow;

        public double DelayOf(string node)
        {
            if (Delays.TryGetValue(node, out var d)) return d;
            return node == Now ? DelayOfNow : 0;
        }
    }

    public sealed class QuotaInfo
    {
        public double Used, Total, Remain, UsedPct, Expire, Upload, Download;
    }

    /// <summary>节点延迟历史（每次测速记一条），用来算丢包率。</summary>
    public sealed class DelayStat
    {
        public double LossRate, Avg, Min, Max, Ok, Fail;
        public List<double> Samples = new List<double>();
    }

    public sealed class ProfileInfo
    {
        public string File = "";
        public double Size, Mtime;
        public bool IsSub;
        public string SubUrl = "";
        public QuotaInfo Quota;
        public double LastUpdate;
        public string LastStatus = "";

        public string Name => System.IO.Path.GetFileNameWithoutExtension(File);
    }

    /// <summary>系统代理的真实状态（/api/snapshot 的 env.sysProxy）。</summary>
    public sealed class SysProxyInfo
    {
        /// <summary>三态：true=开 / false=关 / null=读不到（此时不要渲染成"关"）。</summary>
        public bool? Enabled;
        public bool PointsToOurs;
        public string Server = "";
        public string Pac = "";
        /// <summary>true = 这份数据来自 snapshot 的 env.sysProxy；false = 后端还没落该字段。</summary>
        public bool FromSnapshot;
        /// <summary>后端认为 tools\sysproxy.exe 在不在。界面只当提示用，可点性用壳本地 File.Exists。</summary>
        public bool HasTool;
    }

    /// <summary>端口回落（/api/snapshot 的 env.portFallback）：你填的值非法，已改用默认值。</summary>
    public sealed class PortFallbackInfo
    {
        public string Key = "";
        public string Value = "";
        public int Used;
    }

    public sealed class EnvInfo
    {
        public bool TunEnable, HasSysProxyTool, Autostart, AutoStartCore, AutoStartProxy;
        public bool EnhancedRules = true;
        public string ProxyMode = "smart";
        public bool LogConn = true, LogToFile = true;
        public int SubIntervalHours, AutoDelayMinutes;
        public string TestUrl = "";
        public int DelayTimeout = 5000;
        public bool IsAdmin;
        /// <summary>配置文件读不出来被换掉时，这里是备份文件名（如 config.json.bad-1762…）；否则空串。</summary>
        public string ConfigRecovered = "";

        // ---------------------------------------------------------------- A 批新增
        /// <summary>系统代理真值（三态）。</summary>
        public SysProxyInfo SysProxy = new SysProxyInfo();
        /// <summary>分流规则集是否都加载了。null = 后端还没这个字段（不渲染）。</summary>
        public bool? RulesOk;
        public int RulesOkFail, RulesOkTotal;
        /// <summary>没加载的规则集名字（逗号分隔），告警条上直接点名。</summary>
        public string RulesOkEmpty = "";
        /// <summary>严格模式是否真的生效。null = 不适用（智能模式）或后端还没这个字段。</summary>
        public bool? StrictApplied;
        public string StrictNote = "";
        /// <summary>已保存但未生效的项：proxyMode / enhancedRules / tunEnable / webPort。</summary>
        public List<string> PendingRestart = new List<string>();
        /// <summary>端口回落信息；null = 本次启动没有回落。</summary>
        public PortFallbackInfo PortFallback;
    }

    public sealed class Snap
    {
        /// <summary>
        /// 这份快照可不可信：后端可达 **且** 对面确实是本应用的后端。
        /// false 时下游一律按"未知"渲染（数字显示「—」、开关不显示开/关），
        /// 绝不允许把默认值（0 / false / 空串）当成真值显示 —— 那是本轮的病根。
        /// </summary>
        public bool Trusted;
        public bool CoreRunning, CoreConnected;
        public string CoreVersion = "";
        public int CorePid;
        public double CoreUptime;
        public string Mode = "";
        public string ActiveProfile = "";
        public string CorePath = "";
        public int MixedPort, ControllerPort, WebPort;
        public List<ProfileInfo> Profiles = new List<ProfileInfo>();
        public EnvInfo Env = new EnvInfo();
        public double UpTotal, DownTotal;
        public int Conns;
        public List<GroupInfo> Groups = new List<GroupInfo>();
        public int RulesCount;

        public ProfileInfo Active =>
            Profiles.Find(p => string.Equals(p.File, ActiveProfile, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 三态取值：字段不存在 或 值为 null → <c>null</c>（"读不到"），否则取布尔值。
        /// 不能用 <c>B()</c>：它对缺失键返回 false，于是"读不到"会被渲染成"关"——
        /// 那正是本轮反复在治的"默认值冒充真值"。
        /// </summary>
        private static bool? Tri(JObj o, string key)
        {
            if (!o.Has(key)) return null;
            var v = o.Get(key);
            if (v == null) return null;
            if (v is bool b) return b;
            return null;
        }

        public static Snap From(JObj j)
        {
            var s = new Snap();
            s.Trusted = Api.IsOurs(j);
            if (!j.Exists) return s;

            var core = j["core"];
            s.CoreRunning = core.B("running");
            s.CoreConnected = core.B("connected");
            s.CoreVersion = core.S("version");
            s.CorePid = core.I("pid");
            s.CoreUptime = core.D("uptime");

            var st = j["settings"];
            s.ActiveProfile = st.S("activeProfile");
            s.CorePath = st.S("corePath");
            s.MixedPort = st.I("mixedPort");
            s.ControllerPort = st.I("controllerPort");
            s.WebPort = st.I("webPort");
            s.Mode = j.Has("mode") && j.Get("mode") != null ? j.S("mode") : "";

            foreach (var p in j.List("profiles"))
            {
                var pi = new ProfileInfo
                {
                    File = p.S("file"),
                    Size = p.D("size"),
                    Mtime = p.D("mtime"),
                };
                var sub = p["sub"];
                if (sub.Exists && sub.Has("url"))
                {
                    pi.IsSub = true;
                    pi.SubUrl = sub.S("url");
                    pi.LastUpdate = sub.D("lastUpdate");
                    pi.LastStatus = sub.S("lastStatus");
                }
                var q = p["quota"];
                if (q.Exists && q.D("total") > 0)
                {
                    pi.Quota = new QuotaInfo
                    {
                        Used = q.D("used"),
                        Total = q.D("total"),
                        Remain = q.D("remain"),
                        UsedPct = q.D("usedPct"),
                        Expire = q.D("expire"),
                        Upload = q.D("upload"),
                        Download = q.D("download"),
                    };
                }
                s.Profiles.Add(pi);
            }

            var env = j["env"];
            s.Env = new EnvInfo
            {
                TunEnable = env.B("tunEnable"),
                EnhancedRules = env.Has("enhancedRules") ? env.B("enhancedRules") : true,
                HasSysProxyTool = env.B("hasSysProxyTool"),
                Autostart = env.B("autostart"),
                AutoStartCore = env.B("autoStartCore"),
                AutoStartProxy = env.B("autoStartProxy"),
                AutoDelayMinutes = env.I("autoDelayMinutes"),
                SubIntervalHours = env.I("subIntervalHours"),
                TestUrl = env.S("testUrl"),
                DelayTimeout = env.I("delayTimeout"),
                IsAdmin = env.B("isAdmin"),
                // 这个字段必须在后端 snapshot、EnvInfo 定义、以及这里的解析三处同时存在，
                // 少一处就会静默退回默认值（之前"切完又复原"就是漏了这里的解析）。
                ProxyMode = env.S("proxyMode"),
                LogConn = env.Has("logConn") ? env.B("logConn") : true,
                LogToFile = env.Has("logToFile") ? env.B("logToFile") : true,
                // 同样必须三处同步：后端 snapshot 写了、EnvInfo 有字段、这里解析。
                ConfigRecovered = env.S("configRecovered") ?? "",
            };
            if (string.IsNullOrEmpty(s.Env.ProxyMode)) s.Env.ProxyMode = "smart";

            // ------------------------------------------------------------ A 批新增字段
            // 全部按"有事就说、没有就不渲染"处理：后端没落字段 → 保持 null，界面不显示
            var sp = env["sysProxy"];
            if (sp.Exists)
            {
                s.Env.SysProxy.Enabled = Tri(sp, "enabled");
                s.Env.SysProxy.PointsToOurs = sp.B("pointsToOurs");
                s.Env.SysProxy.Server = sp.S("server");
                s.Env.SysProxy.Pac = sp.S("pac");
                s.Env.SysProxy.HasTool = sp.B("hasTool");
                s.Env.SysProxy.FromSnapshot = true;
            }
            if (env.Has("rulesOk"))
            {
                // 两种形态都兼容：布尔 rulesOk，或对象 {ok, fail, total}
                var rok = env["rulesOk"];
                if (rok.Dict != null)
                {
                    s.Env.RulesOk = Tri(rok, "ok");
                    // 后端实际形状 {ok, empty[], checkedAt}：未加载数量是 empty 的长度，
                    // 不是 fail/total（那两个字段不存在，读它会让文案变成"未加载 0/0"）
                    var empties = rok.StrList("empty");
                    s.Env.RulesOkFail = empties.Count;
                    s.Env.RulesOkEmpty = string.Join(", ", empties);
                    s.Env.RulesOkTotal = rok.I("total");
                }
                else
                {
                    s.Env.RulesOk = Tri(env, "rulesOk");
                    s.Env.RulesOkFail = env.I("rulesFail");
                    s.Env.RulesOkTotal = env.I("rulesTotal");
                }
            }
            if (env.Has("strictApplied"))
            {
                s.Env.StrictApplied = Tri(env, "strictApplied");
                s.Env.StrictNote = env.S("strictNote");
            }
            s.Env.PendingRestart = env.StrList("pendingRestart");
            var pf = env["portFallback"];
            if (pf.Exists)
            {
                s.Env.PortFallback = new PortFallbackInfo
                {
                    Key = pf.S("key"),
                    Value = pf.S("value"),
                    Used = pf.I("used"),
                };
            }
            if (s.Env.DelayTimeout <= 0) s.Env.DelayTimeout = 5000;

            var tr = j["traffic"];
            s.UpTotal = tr.D("upTotal");
            s.DownTotal = tr.D("downTotal");
            s.Conns = tr.I("conns");

            foreach (var g in j.List("groups"))
            {
                var gi = new GroupInfo
                {
                    Name = g.S("name"),
                    Type = g.S("type"),
                    Now = g.S("now"),
                    All = g.StrList("all"),
                    DelayOfNow = g.D("delayOfNow"),
                };
                var dj = g["delays"];
                foreach (var kv in dj.Pairs) gi.Delays[kv.Key] = kv.Value.Num > 0 ? kv.Value.Num : 0;
                s.Groups.Add(gi);
            }

            if (j.Has("rulesCount") && j.Get("rulesCount") != null) s.RulesCount = j.I("rulesCount");
            return s;
        }
    }
}
