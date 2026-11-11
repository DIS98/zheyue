using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClashDeck.Core;

namespace ClashDeck.Views
{
    /// <summary>所有页面的基类：由主窗口统一下发快照，页面自己决定怎么渲染。</summary>
    public abstract class PageBase : UserControl
    {
        public abstract void OnSnap(Snap s);
        public virtual void OnHistory(JObj h) { }
        public virtual void OnActivated() { }
    }
}

namespace ClashDeck.Views
{
    public partial class MainWindow : Window
    {
        private readonly Backend _backend = new Backend();
        private readonly Dictionary<string, PageBase> _pages = new Dictionary<string, PageBase>();
        private readonly string[] _capture;

        private DispatcherTimer _timer;
        private DispatcherTimer _toastTimer;
        private System.Windows.Forms.NotifyIcon _tray;
        private System.Threading.Timer _trayTimer;
        private System.Windows.Forms.ToolStripMenuItem _miState, _miNode, _miToggle, _miProxy, _miDelay;
        private System.Windows.Forms.ToolStripMenuItem _miRule, _miGlobal, _miDirect;

        private Snap _snap = new Snap();
        private string _page = "overview";
        private bool _polling;
        private bool _exiting;
        private bool _ready;
        private int _tick;
        private int _fixPort;                 // 「换个端口」按钮要用的候选端口，0 表示没有
        private bool _noticedConfigRecovery;  // 配置损坏只提示一次，别每两秒弹一遍

        public MainWindow(string[] args)
        {
            InitializeComponent();
            _capture = args;

            // 版本号从程序集读，不再手写（以前写死 v2.0.0，实际早就是 2.0.9）
            VerRelease.Text = "v" + ClashDeck.Core.AppInfo.Release;
            VerFull.Text = "完整 " + ClashDeck.Core.AppInfo.Full;

            // 150% 缩放的 1080p 屏，工作区只有约 1280x690 DIP，写死 806 会被系统压到装不下。
            // 按工作区的 94% 取上限，保证任何缩放下窗口都完整可见。
            var wa = SystemParameters.WorkArea;
            if (Width > wa.Width * 0.96) Width = Math.Max(MinWidth, wa.Width * 0.96);
            if (Height > wa.Height * 0.94) Height = Math.Max(MinHeight, wa.Height * 0.94);

            // --width/--height：给截图验证用。窗口最小宽度下工具栏最容易挤爆，
            // 只测默认尺寸等于没测（用户一缩窗口问题就回来）。
            if (int.TryParse(ArgValue("--width"), out var cw) && cw >= MinWidth && cw <= 4000) Width = cw;
            if (int.TryParse(ArgValue("--height"), out var ch) && ch >= MinHeight && ch <= 3000) Height = ch;

            SyncTitleBarToTheme();
            UpdateThemeButton();
            BuildTray();

            // 关机 / 注销：WPF 不会走托盘退出，必须自己接。这里只做"关代理"这一件事，
            // 而且要同步做完（后面所有进程都会被系统杀掉，没法等异步）。
            try
            {
                Application.Current.SessionEnding += (_, __) =>
                {
                    _exiting = true;
                    try
                    {
                        if (_backend.WeOwnIt) SysProxy.DirectProxyOff(SysProxy.Caller.Exit);
                    }
                    catch (Exception ex) { Paths.WriteLog("session ending off proxy: " + ex.Message); }
                };
            }
            catch (Exception ex) { Paths.WriteLog("SessionEnding hook failed: " + ex.Message); }

            Loaded += OnLoadedAsync;
        }

        // ================================================================ 启动

        private async void OnLoadedAsync(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoadedAsync;
            if (App.IsDark) SyncTitleBarToTheme();
            await BootAsync();
        }

        private async Task BootAsync()
        {
            Splash.Visibility = Visibility.Visible;
            SplashRetry.Visibility = Visibility.Collapsed;
            SplashFixPort.Visibility = Visibility.Collapsed;
            _fixPort = 0;
            SplashText.Foreground = (Brush)FindResource("Fg3");
            SplashText.Text = "正在启动后端…";

            string msg;
            try
            {
                msg = await _backend.StartAsync();
            }
            catch (Exception ex)
            {
                Paths.WriteLog("boot failed: " + ex);
                msg = "后端启动异常：" + ex.Message;
            }
            Paths.WriteLog("boot: " + msg);

            if (!_backend.Ready)
            {
                var fatal = _backend.Fatal;
                if (fatal != null && fatal.Code == "EADDRINUSE")
                {
                    // 必须先确定候选端口，再拼文案 —— 文案里要提到那个端口号。
                    // 反过来的话 _fixPort 还是 0，正文会走"反复失败请看日志"那一条，
                    // 而下面却摆着一个「改用端口 48711」的按钮，自相矛盾。
                    if (fatal.Free.Count > 0)
                    {
                        _fixPort = fatal.Free[0];
                        SplashFixPort.Content = "改用端口 " + _fixPort;
                        SplashFixPort.Visibility = Visibility.Visible;
                    }
                    SplashTitle.Text = "端口 " + fatal.Port + " 被占用，后端没能启动";
                    SplashText.Text = BuildPortBusyText(fatal);
                }
                else
                {
                    SplashTitle.Text = "后端没能启动";
                    SplashText.Text = msg + "\n\n可以点下面的按钮重试；若反复失败，请看 logs\\app.log。";
                }
                SplashText.Foreground = (Brush)FindResource("Bad");
                SplashRetry.Visibility = Visibility.Visible;
                if (_capture != null)
                {
                    // 启动失败的界面恰恰是最需要留证据的地方（以前这里直接退出，只能靠文字描述）。
                    Paths.WriteLog("capture: backend not ready，截启动失败界面");
                    var shot = ArgValue("--capture");
                    if (!string.IsNullOrEmpty(shot)) await ShootAsync(shot, 1200);
                    ReallyExit();
                }
                return;
            }

            Splash.Visibility = Visibility.Collapsed;
            _ready = true;

            ShowPage("overview");
            EnsurePollTimer();
            await PollAsync();

            // 配置读不出来时后端会按默认值启动 —— 端口、订阅、分流模式全变回默认。
            // 以前这件事只写进 startup-error.log（没人会去翻），用户只觉得"设置怎么没了"。
            if (!_noticedConfigRecovery && _snap != null && !string.IsNullOrEmpty(_snap.Env.ConfigRecovered))
            {
                _noticedConfigRecovery = true;
                var bk = _snap.Env.ConfigRecovered;
                ShowToast(bk == "(备份失败)"
                    ? "配置文件读不出来，已按默认设置启动"
                    : "配置文件读不出来，已按默认设置启动；原文件在 data\\" + bk, false);
                Paths.WriteLog("config recovered: " + bk);
            }

            await HandleCaptureAsync();
        }

        /// <summary>
        /// 端口被占时的提示文案。两件事必须一起说清楚：
        /// ① 端口被占了（用户最先看到的表象）；
        /// ② 配置本来就坏了、现在跑的是默认值 —— 否则用户会去设置里找"我明明设的是别的端口"，
        ///    越查越乱，而且不知道订阅和分流设置也没能读出来。
        /// </summary>
        private string BuildPortBusyText(Core.FatalInfo f)
        {
            var sb = new StringBuilder();
            sb.Append("这个端口上已经有程序在监听（可能是另一个实例，也可能是别的软件）。");

            if (!string.IsNullOrEmpty(f.ConfigBackup) || !string.IsNullOrEmpty(f.ConfigReason))
            {
                var why = string.IsNullOrEmpty(f.ConfigReason) ? "" : "（" + Shorten(f.ConfigReason, 60) + "）";
                sb.Append("\n\n另外：配置文件也读不出来").Append(why).Append("。\n");
                // 文件名单独占一行：13 位时间戳会把一整句话挤成三四行，读不下去。
                sb.Append(string.IsNullOrEmpty(f.ConfigBackup)
                    ? "备份没能做成（文件可能正被占用）。"
                    : "已备份成：data\\" + f.ConfigBackup);
                sb.Append("\n这次是按默认设置启动的，所以上面这个端口号是默认值，不是你原来设的那个。\n")
                  .Append("想找回原来的设置，把那个备份文件改名回 config.json 即可。");
            }

            if (_fixPort > 0)
            {
                sb.Append("\n\n可以点「重试」，或者让程序换成端口 ").Append(_fixPort).Append(" 重开");
                // 配置已经坏了的时候说"只改端口、其它不动"是误导：那份配置里本来就没有别的东西，
                // 换端口会把默认值写成一个新的 config.json。
                sb.Append(string.IsNullOrEmpty(f.ConfigBackup)
                    ? "（只改面板端口，其它设置不变）。"
                    : "（会按默认设置写一份新配置，旧文件仍保留为上面的备份）。");
            }
            else
            {
                sb.Append("\n\n可以点下面的按钮重试；若反复失败，请看 logs\\app.log。");
            }
            return sb.ToString();
        }

        private static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private async void SplashFixPort_Click(object sender, RoutedEventArgs e)
        {
            if (_fixPort <= 0) return;
            int port = _fixPort;
            SplashFixPort.IsEnabled = false;
            SplashText.Foreground = (Brush)FindResource("Fg3");
            SplashText.Text = "正在把面板端口改成 " + port + "…";

            var err = await _backend.SetWebPortAsync(port);
            SplashFixPort.IsEnabled = true;
            if (!string.IsNullOrEmpty(err))
            {
                SplashText.Foreground = (Brush)FindResource("Bad");
                SplashText.Text = err + "\n\n可以点「重试」再试一次。";
                return;
            }
            Paths.WriteLog("web port switched to " + port);
            // 界面还得连新端口，否则后端起在 48711、界面一直探旧端口，报"10 秒未响应"。
            Api.BaseUrl = "http://127.0.0.1:" + port;
            await BootAsync();
        }

        private async void SplashRetry_Click(object sender, RoutedEventArgs e) => await BootAsync();

        // ================================================================ 轮询

        private async Task PollAsync()
        {
            if (_polling || !_ready || _exiting) return;
            _polling = true;
            try
            {
                var snapTask = Api.SnapshotModel();
                var histTask = Api.History(600);
                await Task.WhenAll(snapTask, histTask);

                _snap = snapTask.Result;
                var hist = histTask.Result;

                UpdateChrome(_snap);
                if (_pages.TryGetValue(_page, out var page)) page.OnSnap(_snap);
                if (_pages.TryGetValue(_page, out var p2) && hist.Exists) p2.OnHistory(hist);

                if (++_tick % 3 == 0) await RefreshTrayAsync();
            }
            catch (Exception ex) { Paths.WriteLog("poll: " + ex.Message); }
            finally { _polling = false; }
        }

        private void UpdateChrome(Snap s)
        {
            UpdateBadges(s);
            var dotBrush = (Brush)FindResource(s.CoreRunning ? "Ok" : "Fg3");
            TopDot.Fill = dotBrush;
            SideDot.Fill = dotBrush;

            string state;
            if (!s.CoreRunning) state = "未启动";
            else state = "运行中" + (string.IsNullOrEmpty(s.CoreVersion) ? "" : " v" + s.CoreVersion);

            TopState.Text = state;
            SideState.Text = state;
            SideState.Foreground = (Brush)FindResource(s.CoreRunning ? "Fg" : "Fg2");

            var g = s.Groups.Count > 0 ? s.Groups[0] : null;
            // 侧栏和代理页必须用同一套显示规则，否则同一个节点在这里是空白、在那里是 JP
            SideNode.Text = g != null && !string.IsNullOrEmpty(g.Now) ? Fmt.NormalizeFlags(g.Now) : "—";
            SideNode.ToolTip = g != null ? g.Now : "";

            BtnToggle.Content = s.CoreRunning ? "停止" : "启动";
            BtnRestart.IsEnabled = s.CoreRunning;
            // 失联态下"启动"点了只会失败（后端已死），徒增一次"没反应"的体验；
            // 真正的主动作是横幅里的「重启后端」。
            BtnToggle.IsEnabled = s.Trusted;
        }

        // ================================================================ 对外小接口（给各页用）

        /// <summary>当前快照里的端口（0 表示还没拿到快照）。</summary>
        public int CurrentWebPort => _snap == null ? 0 : _snap.WebPort;
        public int CurrentMixedPort => _snap == null ? 0 : _snap.MixedPort;
        public int CurrentControllerPort => _snap == null ? 0 : _snap.ControllerPort;

        /// <summary>把面板端口写进 config.json（端口被占时 API 是断的，只能走这条一次性命令）。</summary>
        public Task<string> SetWebPortAsync(int port) => _backend.SetWebPortAsync(port);

        // ================================================================ 徽章（A8）

        private string _badgeSig = "";

        /// <summary>
        /// 标题右侧徽章。判据：徽章只说"此刻配置与运行态的关系"，
        /// 不写"刚才发生了什么"——后者三天后就是假话（契约零-b）。
        /// 多个待重启项合并成**一个**徽章、点开列明细：四个徽章并排等于没设计。
        /// </summary>
        private void UpdateBadges(Snap s)
        {
            var sig = (s.Trusted ? "1" : "0") + "|"
                      + (s.Env.StrictApplied == null ? "-" : (s.Env.StrictApplied.Value ? "1" : "0")) + "|"
                      + string.Join(",", s.Env.PendingRestart.ToArray());
            if (sig == _badgeSig) return;
            _badgeSig = sig;
            BadgeHost.Children.Clear();
            if (!s.Trusted) return;

            if (s.Env.StrictApplied == false)
            {
                AddBadge("严格未生效", "BadSoft", "Bad",
                    "严格模式本次没有生效："
                    + (string.IsNullOrEmpty(s.Env.StrictNote) ? "订阅里没有主策略组，或没有兜底行" : s.Env.StrictNote));
            }

            var pr = s.Env.PendingRestart;
            if (pr != null && pr.Count > 0)
            {
                var needApp = pr.Contains("webPort");
                var lines = pr.Select(k => "· " + PendingName(k)).ToList();
                if (needApp) lines.Add("（重启应用会一并停掉内核，重启后无需再单独重启内核）");
                AddBadge(pr.Count + " 项未生效 · " + (needApp ? "重启应用" : "重启内核") + "生效",
                    "WarnSoft", "Warn", string.Join("\n", lines.ToArray()));
            }
        }

        private static string PendingName(string key)
        {
            switch (key)
            {
                case "proxyMode": return "分流模式";
                case "enhancedRules": return "增强分流";
                case "tunEnable": return "TUN";
                case "webPort": return "面板端口";
                default: return key;
            }
        }

        private void AddBadge(string text, string bgKey, string fgKey, string detail)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = (Brush)FindResource(fgKey),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var b = new Border
            {
                Style = (Style)FindResource("Pill"),
                Background = (Brush)FindResource(bgKey),
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(0, 0, 6, 0),
                Child = tb,
                ToolTip = detail,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.MouseLeftButtonUp += (_, __) =>
            {
                try { MessageBox.Show(detail, text, MessageBoxButton.OK, MessageBoxImage.Information); }
                catch { }
            };
            BadgeHost.Children.Add(b);
        }

        // ================================================================ 导航

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (!(sender is RadioButton rb) || rb.Tag is not string key) return;
            if (!_ready) { _page = key; return; }
            ShowPage(key);
        }

        private void ShowPage(string key)
        {
            _page = key;
            if (!_pages.TryGetValue(key, out var page))
            {
                page = CreatePage(key);
                if (page == null) return;
                _pages[key] = page;
            }

            PageHost.Content = page;
            var (title, sub) = Meta(key);
            PageTitle.Text = title;
            PageSub.Text = sub;
            page.OnActivated();
            if (_snap != null && _snap.Profiles != null) page.OnSnap(_snap);
        }

        private PageBase CreatePage(string key)
        {
            switch (key)
            {
                case "overview": return new OverviewView(this);
                case "proxies": return new ProxiesView(this);
                case "conns": return new ConnsView(this);
                case "rules": return new RulesView(this);
                case "logs": return new LogsView(this);
                case "profiles": return new ProfilesView(this);
                default: return null;
            }
        }

        private static (string, string) Meta(string key)
        {
            switch (key)
            {
                case "overview": return ("概览", "实时流量、接管方式与内核状态");
                case "proxies": return ("代理", "策略组与节点，点延迟数字可单独测速");
                case "conns": return ("连接", "活动连接，点行看详情，可按进程聚合");
                case "rules": return ("规则", "命中排序、规则预判与规则集更新");
                case "logs": return ("日志", "内核输出 / 连接记录 / 面板动作，可按来源过滤");
                case "profiles": return ("配置", "订阅、测速、端口与备份");
                default: return ("折跃", "");
            }
        }

        // ================================================================ 顶栏动作

        private async void BtnToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            BtnToggle.IsEnabled = false;
            var running = _snap.CoreRunning;
            try
            {
                var j = running
                    ? await Api.Post("/api/core/stop", null, 20000)
                    : await Api.Post("/api/core/start", null, 40000);
                // A14：ok 表示"这一层调用成功"，ready 才表示"内核真的能用了"。
                // 内核进程起来但控制口 8 秒没响应时，ok 仍是 true —— 这时候说"已启动"
                // 就是绿的假话，所以按 ready 渲染黄态并继续轮询。
                if (!running && j.B("ok") && j.Has("ready") && !j.B("ready"))
                    Toast(j.S("msg") ?? "内核已起，控制口未响应", true, warn: true);
                else
                    Toast(j.S("msg"), j.B("ok"));
            }
            finally { BtnToggle.IsEnabled = true; }
            await PollAsync();
        }

        private async void BtnRestart_Click(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            BtnRestart.IsEnabled = false;
            BtnToggle.IsEnabled = false;
            try
            {
                var j = await Api.Post("/api/core/restart", null, 45000);
                Toast(j.S("msg"), j.B("ok"));
            }
            finally { BtnRestart.IsEnabled = true; BtnToggle.IsEnabled = true; }
            await PollAsync();
        }

        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            App.ToggleTheme();
            UpdateThemeButton();
            ReloadCurrentPage();
        }

        /// <summary>
        /// 切主题只是换掉资源字典，XAML 里的 {DynamicResource} 会跟着变；
        /// 但代码里用 Res("...") 取到的是**那一刻的 Brush 实例**，不会更新 ——
        /// 表现是：停在日志页切深色，已有日志行还是浅色主题的深灰字，深色背景上看不见。
        /// 所以切完让当前页重建一次。
        /// </summary>
        private void ReloadCurrentPage()
        {
            if (!_pages.TryGetValue(_page, out var page)) return;
            page.OnActivated();
            if (_snap != null) page.OnSnap(_snap);
        }

        private void UpdateThemeButton()
        {
            ThemeIcon.Text = App.IsDark ? "\uE706" : "\uE708";
            ThemeText.Text = App.IsDark ? "切换到浅色" : "切换到深色";
        }

        public void SyncTitleBarToTheme()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int v = App.IsDark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(hwnd, 19, ref v, sizeof(int)); // Win10 1809 早期版本用 19
            }
            catch { }
        }

        [DllImport("dwmapi.dll", CharSet = CharSet.Unicode)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // ================================================================ 轻提示

        private void Toast(string text, bool ok = true, bool warn = false)
        {
            // 失败但后端没给文案（失联时 msg 是 null）—— 不能静默返回，
            // 否则"点按钮完全没反应"就成了一种常态。
            if (string.IsNullOrEmpty(text))
            {
                if (ok) return;
                text = "操作失败（后端无响应）";
            }
            ToastText.Text = text;
            ToastBox.Background = (Brush)FindResource(ok ? (warn ? "WarnSoft" : "Panel") : "BadSoft");
            ToastText.Foreground = (Brush)FindResource(ok ? (warn ? "Warn" : "Fg") : "Bad");
            ToastBox.Visibility = Visibility.Visible;
            ToastBox.Opacity = 1;

            if (_toastTimer == null)
            {
                _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
                _toastTimer.Tick += (_, __) => { _toastTimer.Stop(); ToastBox.Visibility = Visibility.Collapsed; };
            }
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        public void ShowToast(string text, bool ok = true) => Toast(text, ok);

        /// <summary>页面改完配置后立刻拉一次，不用等下一个 2 秒。</summary>
        public async System.Threading.Tasks.Task RefreshNow() => await PollAsync();

        /// <summary>供页面跳转（概览的“去配置”这类）。</summary>
        public void GoPage(string key)
        {
            var rb = FindNav(key);
            if (rb != null) rb.IsChecked = true;
        }

        // ================================================================ 托盘

        private void BuildTray()
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();

            _miState = new System.Windows.Forms.ToolStripMenuItem("状态：连接中…") { Enabled = false };
            _miNode = new System.Windows.Forms.ToolStripMenuItem("当前节点：—") { Enabled = false };

            _miRule = new System.Windows.Forms.ToolStripMenuItem("规则模式", null, async (_, __) => await SetMode("rule"));
            _miGlobal = new System.Windows.Forms.ToolStripMenuItem("全局模式", null, async (_, __) => await SetMode("global"));
            _miDirect = new System.Windows.Forms.ToolStripMenuItem("直连模式", null, async (_, __) => await SetMode("direct"));
            var miMode = new System.Windows.Forms.ToolStripMenuItem("代理模式");
            miMode.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] { _miRule, _miGlobal, _miDirect });

            _miDelay = new System.Windows.Forms.ToolStripMenuItem("刷新延迟", null, async (_, __) => await RefreshDelayTray());
            _miToggle = new System.Windows.Forms.ToolStripMenuItem("启动内核", null, async (_, __) => await ToggleCoreTray());
            _miProxy = new System.Windows.Forms.ToolStripMenuItem("系统代理：未知", null, async (_, __) => await ToggleProxyTray());

            menu.Items.Add(_miState);
            menu.Items.Add(_miNode);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            var miShow = new System.Windows.Forms.ToolStripMenuItem("显示窗口", null, (_, __) => ShowFromTray());
            miShow.Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold);
            menu.Items.Add(miShow);
            menu.Items.Add(miMode);
            menu.Items.Add(_miDelay);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(_miToggle);
            menu.Items.Add(_miProxy);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("退出", null, (_, __) => ReallyExit()));

            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "折跃",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.DoubleClick += (_, __) => ShowFromTray();
            Paths.WriteLog("tray ready");

            _trayTimer = new System.Threading.Timer(async _ =>
            {
                try { await RefreshTrayAsync().ConfigureAwait(false); }
                catch (Exception ex) { Paths.WriteLog("tray: " + ex.Message); }
            }, null, 1500, 5000);
        }

        private static System.Drawing.Icon LoadAppIcon()
        {
            try
            {
                var p = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                {
                    var ic = System.Drawing.Icon.ExtractAssociatedIcon(p);
                    if (ic != null) return ic;
                }
            }
            catch { }
            return System.Drawing.SystemIcons.Shield;
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }

        private async Task RefreshTrayAsync()
        {
            if (_tray == null || _exiting) return;
            var s = await Api.SnapshotModel().ConfigureAwait(false);
            var status = await Api.SysProxyStatus().ConfigureAwait(false);

            string state = !s.CoreRunning ? "内核未运行"
                : $"运行中 v{s.CoreVersion} · {s.Conns} 连接";
            var g = s.Groups.Count > 0 ? s.Groups[0] : null;
            string node = g != null && !string.IsNullOrEmpty(g.Now) ? Fmt.NormalizeFlags(g.Now) : "—";

            string proxy = "";
            if (status.Exists)
                proxy = status.B("enabled")
                    ? (status.B("pointsToOurs") ? "系统代理：开（本应用）" : "系统代理：开（外部）")
                    : "系统代理：关";

            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _tray.Text = Cut("折跃 — " + state, 63);
                    _miState.Text = "状态：" + state;
                    _miNode.Text = "当前节点：" + node;
                    _miToggle.Text = s.CoreRunning ? "停止内核" : "启动内核";
                    if (proxy.Length > 0) _miProxy.Text = proxy;
                    _miRule.Checked = s.Mode == "rule";
                    _miGlobal.Checked = s.Mode == "global";
                    _miDirect.Checked = s.Mode == "direct";
                    _miDelay.Enabled = s.CoreRunning;
                    _snap = s;
                }
                catch { }
            }));
        }

        // 名字不能叫 Clip：UIElement 上本来就有一个 Clip 属性，同名的静态方法会把它遮住（CS0108），
        // 以后谁在这个类里写 Clip 都会先命中这个字符串截断，属于埋雷。
        private static string Cut(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

        private async Task SetMode(string mode)
        {
            var j = await Api.Post("/api/mode", Json.Obj("mode", mode));
            Paths.WriteLog($"tray mode {mode} -> {j.S("msg")}");
            await RefreshTrayAsync();
            await PollAsync();
        }

        private async Task ToggleCoreTray()
        {
            var running = _snap.CoreRunning;
            var j = running
                ? await Api.Post("/api/core/stop", null, 20000)
                : await Api.Post("/api/core/start", null, 40000);
            Paths.WriteLog($"tray core {(running ? "stop" : "start")} -> {j.S("msg")}");
            await RefreshTrayAsync();
            await PollAsync();
        }

        private async Task ToggleProxyTray()
        {
            var st = await Api.SysProxyStatus();
            var want = !(st.Exists && st.B("enabled"));
            var j = await Api.Post("/api/systemproxy", Json.Obj("enable", want), 15000);
            Paths.WriteLog($"tray proxy -> {want}: {j.S("msg")}");
            await RefreshTrayAsync();
            await PollAsync();
        }

        private async Task RefreshDelayTray()
        {
            var g = _snap.Groups.Count > 0 ? _snap.Groups[0] : null;
            if (g == null) return;
            Paths.WriteLog("tray delay start " + g.Name);
            var j = await Api.Get("/api/proxies/delay?group=" + Api.Q(g.Name) + "&timeout=5000", 60000);
            Paths.WriteLog($"tray delay {g.Name} ok={j.B("ok")}");
            await PollAsync();
        }

        // ================================================================ 重启后端（失联态的主动作）

        /// <summary>
        /// 失联态唯一有意义的主动作：重启**后端**（不是 /api/core/restart ——
        /// 那个要后端还活着才有效，转到死掉的后端上自然没反应）。
        /// 三步：先收尸 → 再 StartAsync → 收尸失败撞端口就落到既有的「改用端口」界面。
        /// </summary>
        public async Task RestartBackendAsync()
        {
            Paths.WriteLog("restart backend requested");
            Toast("正在重启后端…", true);
            try
            {
                if (_backend.WeOwnIt)
                    await Task.Run(() => _backend.Dispose());
                else
                    Backend.TryKillZombie();
            }
            catch (Exception ex) { Paths.WriteLog("restart backend: cleanup failed " + ex.Message); }

            string msg;
            try { msg = await _backend.StartAsync(); }
            catch (Exception ex) { msg = "后端重启异常：" + ex.Message; }
            Paths.WriteLog("restart backend: " + msg);

            if (_backend.Ready)
            {
                if (!_ready)
                {
                    _ready = true;
                    EnsurePollTimer();
                }
                Splash.Visibility = Visibility.Collapsed;
                await PollAsync();
                Toast("后端已恢复", true);
            }
            else
            {
                Toast(msg, false);
            }
        }

        /// <summary>
        /// 快照轮询计时器只有一个：启动与"后端恢复"两处都要它，
        /// 各写一遍就会出现两个 2 秒计时器同时打 /api/snapshot（请求直接翻倍）。
        /// </summary>
        private void EnsurePollTimer()
        {
            if (_timer == null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _timer.Tick += async (_, __) => await PollAsync();
            }
            if (!_timer.IsEnabled) _timer.Start();
        }

        // ================================================================ 退出

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_exiting)
            {
                e.Cancel = true;
                Hide();
                try
                {
                    _tray?.ShowBalloonTip(1600, "折跃", "已收进托盘，右键图标可退出。", System.Windows.Forms.ToolTipIcon.Info);
                }
                catch { }
            }
            base.OnClosing(e);
        }

        /// <summary>
        /// 退出绝不能在 UI 线程上同步等异步（WinForms 版就是 GetAwaiter().GetResult() 死锁的）。
        /// 这里全程 await，另加 2.5 秒强制兜底。
        /// </summary>
        public async void ReallyExit()
        {
            if (_exiting) return;
            _exiting = true;
            Paths.WriteLog("exit requested");

            try { _timer?.Stop(); } catch { }
            try { _trayTimer?.Dispose(); } catch { }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } } catch { }

            IsHitTestVisible = false;

            // 关代理放在最前面：它是唯一"不做就会断网"的动作，而且不依赖后端（后端可能已经死了）。
            // 顺序很关键 —— 先停内核再关代理的话，中间那段时间浏览器就指着死端口了。
            try
            {
                if (_backend.WeOwnIt)
                    SysProxy.DirectProxyOff(SysProxy.Caller.Exit);
            }
            catch (Exception ex) { Paths.WriteLog("exit off proxy: " + ex.Message); }

            try
            {
                if (_backend.WeOwnIt)
                {
                    await _backend.StopCoreAsync();
                    Paths.WriteLog("core stopped");
                }
            }
            catch (Exception ex) { Paths.WriteLog("exit stop core: " + ex.Message); }

            Hide();
            try { await Task.Run(() => _backend.Dispose()); } catch { }

            // 兜底：万一有东西卡住消息循环，6 秒后无条件结束进程。
            // 不能设 2.5 秒 —— 后端停内核最坏要等 6 秒（内核退出）+ 3 秒（端口释放），
            // 抢跑会把后端连根杀掉，导致"系统代理还指着已退出的内核"→ 整机网页转圈打不开。
            // 真正的安全网在服务端：下次启动时 healStaleProxy() 会清掉残留代理。
            new System.Threading.Timer(_ => Environment.Exit(0), null, 6000, Timeout.Infinite);
            Paths.WriteLog("exiting");
            Application.Current.Shutdown();
        }

        // ================================================================ 截图（开发用）

        private async Task HandleCaptureAsync()
        {
            var path = ArgValue("--capture");
            if (string.IsNullOrEmpty(path)) return;

            if (ArgValue("--theme") == "dark")
            {
                App.ApplyTheme(true);
                UpdateThemeButton();
                SyncTitleBarToTheme();
            }

            var page = ArgValue("--page");
            if (!string.IsNullOrEmpty(page))
            {
                var rb = FindNav(page);
                if (rb != null) rb.IsChecked = true;
            }

            if (HasArg("--autostart"))
            {
                var st = await Api.Post("/api/core/start", null, 40000);
                Paths.WriteLog("capture autostart: " + st.S("msg"));
                await Task.Delay(5200);
                await PollAsync();
            }

            if (HasArg("--userflow") && _snap.CoreRunning)
            {
                await RunUserFlowAsync();
            }

            if (HasArg("--warm") && _snap.CoreRunning)
            {
                await WarmTrafficAsync(_snap.MixedPort);
                await PollAsync();
            }

            // 默认 2600ms：等首轮数据把卡片填满。
            // --capture-delay <ms> 可以等更久 —— 用来抓"过一会儿才出现"的状态
            // （最典型的是失联：它要等下一次 2 秒轮询失败才会出现，2.6 秒的窗口抓不到）。
            var shootDelay = 2600;
            if (int.TryParse(ArgValue("--capture-delay"), out var dly) && dly > 0 && dly <= 120000)
                shootDelay = dly;
            await Task.Delay(shootDelay);
            await ShootAsync(path);
            ReallyExit();
        }

        /// <summary>把当前窗口渲染成 PNG。截图失败只写日志，不影响主流程。</summary>
        private async Task ShootAsync(string path, int waitMs = 0)
        {
            if (waitMs > 0) await Task.Delay(waitMs);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                // 只渲染客户区内容：Window 的 ActualHeight 含标题栏/边框，
                // 直接按它建画布会在底部留一条空白（深色主题下尤其明显）。
                // 直接渲染窗口本体，画布取客户区尺寸：
                // Window 的 Visual 原点就在客户区左上角，这样既不含标题栏也不留空白。
                var root = Content as FrameworkElement;
                var w = (int)Math.Ceiling(root != null ? root.ActualWidth : RenderSize.Width);
                var h = (int)Math.Ceiling(root != null ? root.ActualHeight : RenderSize.Height);
                if (w < 200 || h < 200) { w = (int)ActualWidth; h = (int)ActualHeight; }
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(this);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(path)) enc.Save(fs);
                Paths.WriteLog($"capture -> {path} ({w}x{h})");
            }
            catch (Exception ex) { Paths.WriteLog("capture failed: " + ex.Message); }
        }

        /// <summary>
        /// 复现用户报的那条路径：切规则模式 → 选一个真实节点 → 开系统代理 → 更新订阅，
        /// 每一步都把"快照里的节点数"和"界面里实际渲染的节点数"写进日志对照。
        /// 只在 --userflow 时执行。
        /// </summary>
        private async Task RunUserFlowAsync()
        {
            Paths.WriteLog($"userflow start: mode={_snap.Mode} profile={_snap.ActiveProfile}");

            var r1 = await Api.Post("/api/mode", Json.Obj("mode", "rule"), 10000);
            Paths.WriteLog("userflow mode=rule -> " + r1.S("msg"));
            await Task.Delay(800);
            await PollAsync();

            var g = _snap.Groups.FirstOrDefault(x => x.Type == "Selector" && x.Name != "GLOBAL")
                    ?? _snap.Groups.FirstOrDefault();
            if (g != null)
            {
                var picked = g.All.FirstOrDefault(n => n != "DIRECT" && n != "REJECT"
                    && n.IndexOf("剩余流量", StringComparison.Ordinal) < 0
                    && n.IndexOf("到期", StringComparison.Ordinal) < 0
                    && n.IndexOf("官网", StringComparison.Ordinal) < 0
                    && n.IndexOf("重置", StringComparison.Ordinal) < 0);
                if (picked != null)
                {
                    var r2 = await Api.Post("/api/proxies/select", Json.Obj("group", g.Name, "name", picked), 10000);
                    Paths.WriteLog($"userflow select [{g.Name}] {picked} -> {r2.S("msg")}");
                }
            }
            await Task.Delay(900);
            await PollAsync();

            var r3 = await Api.Post("/api/systemproxy", Json.Obj("enable", true), 15000);
            Paths.WriteLog("userflow sysproxy on -> " + r3.S("msg"));
            await Task.Delay(1200);
            await PollAsync();
            DumpUi("after-sysproxy");

            var profile = _snap.ActiveProfile;
            if (!string.IsNullOrEmpty(profile))
            {
                var r4 = await Api.Post("/api/subscriptions/update", Json.Obj("file", profile), 120000);
                Paths.WriteLog($"userflow update [{profile}] -> {r4.S("msg")}");
            }
            await Task.Delay(3000);

            for (int i = 0; i < 3; i++) { await PollAsync(); await Task.Delay(700); }
            DumpUi("after-update");

            await Api.Post("/api/systemproxy", Json.Obj("enable", false), 15000);
            Paths.WriteLog("userflow done");
        }

        private void DumpUi(string tag)
        {
            string snapInfo = "";
            foreach (var g in _snap.Groups) snapInfo += $"{g.Name}={g.All.Count} ";
            Paths.WriteLog($"UISTATE [{tag}] snapshot: {snapInfo}");
            if (_pages.TryGetValue("proxies", out var p) && p is ProxiesView pv) pv.DumpState();
        }

        /// <summary>
        /// 通过本机内核端口发几个真实请求，让连接列表和流量曲线有数据。
        /// 仅用于截图/自检，正常运行时不会走到。
        /// </summary>
        private async Task WarmTrafficAsync(int port)
        {
            try
            {
                var handler = new System.Net.Http.HttpClientHandler
                {
                    Proxy = new System.Net.WebProxy("http://127.0.0.1:" + port),
                    UseProxy = true,
                };
                using (var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) })
                {
                    var urls = new[]
                    {
                        "https://www.gstatic.com/generate_204",
                        "https://api.github.com",
                        "https://cdn.jsdelivr.net/npm/vue/package.json",
                        "https://www.cloudflare.com/cdn-cgi/trace",
                    };
                    foreach (var u in urls)
                    {
                        try { await http.GetAsync(u); } catch { /* 单个失败不影响 */ }
                    }
                }
                Paths.WriteLog("warm traffic done");
            }
            catch (Exception ex) { Paths.WriteLog("warm traffic failed: " + ex.Message); }
        }

        private RadioButton FindNav(string page)
        {
            switch (page)
            {
                case "overview": return NavOverview;
                case "proxies": return NavProxies;
                case "conns": return NavConns;
                case "rules": return NavRules;
                case "logs": return NavLogs;
                case "profiles": return NavProfiles;
                default: return null;
            }
        }

        private bool HasArg(string name) => Array.IndexOf(_capture, name) >= 0;

        private string ArgValue(string name)
        {
            for (int i = 0; i < _capture.Length - 1; i++)
                if (_capture[i] == name) return _capture[i + 1];
            return null;
        }
    }
}
