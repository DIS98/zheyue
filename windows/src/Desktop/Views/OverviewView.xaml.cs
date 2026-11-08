using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClashDeck.Core;

namespace ClashDeck.Views
{
    public partial class OverviewView : PageBase
    {
        private readonly MainWindow _owner;
        private readonly DispatcherTimer _usageTimer;
        private string _groupSig = "";
        private string _modeSig = "";
        private bool _updating;
        private Snap _lastSnap;

        public OverviewView(MainWindow owner)
        {
            _owner = owner;
            InitializeComponent();

            _usageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            // 和日志页/连接页保持一致：不在这个页面就别拉数据
            _usageTimer.Tick += async (_, __) => { if (IsVisible) await LoadUsageAsync(); };
            _usageTimer.Start();
        }

        private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        private Style St(string key) => TryFindResource(key) as Style;

        private bool _autoChecked;

        public override async void OnActivated()
        {
            await LoadUsageAsync();
            await ShowAutoUpdateAsync();
        }

        /// <summary>
        /// 启动时后端已经静默查过一次更新，这里直接把结果摆出来。
        /// 以前必须手动点「检查更新」才知道有没有新版，等于没有。
        /// </summary>
        private async Task ShowAutoUpdateAsync()
        {
            if (_autoChecked) return;
            _autoChecked = true;
            try
            {
                var j = await Api.Get("/api/update/auto", 8000);
                var info = j["info"];
                if (info is JObj o && o.B("ok"))
                {
                    ShowUpdateChecked(o);
                    return;
                }
                // 后端还没查完（启动 8 秒内）：稍后再看一次
                await Task.Delay(9000);
                var j2 = await Api.Get("/api/update/auto", 8000);
                if (j2["info"] is JObj o2 && o2.B("ok")) ShowUpdateChecked(o2);
            }
            catch { /* 自动检查失败不打扰用户 */ }
        }

        // ============================================================ 快照

        public override void OnSnap(Snap s)
        {
            if (s == null) return;
            _lastSnap = s;
            // 失联 → 可信的那一次转换，才清掉"刚刚已关闭"的留痕（留到真值能复核为止）
            if (s.Trusted && !_lastTrusted) _localOffNote = null;
            _lastTrusted = s.Trusted;

            if (!s.Trusted)
            {
                // 不可信：数字一律按"未知"处理，不做局部覆盖 —— 否则会出现
                // "曲线空了但上下行停在旧值""日志页挂着陈旧的 N 行"这类半新半旧。
                ConnCount.Text = "—";
                _updating = true;
                try { UpdateBanner(s); UpdateSysProxy(s); }
                finally { _updating = false; }
                return;
            }

            _lastConns = s.Conns;
            ConnCount.Text = s.Conns.ToString();
            _updating = true;
            try
            {
                UpdateBanner(s);
                UpdateKv(s);
                UpdateSwitches(s);
                UpdateGroup(s);
            }
            finally { _updating = false; }
        }

        private void UpdateBanner(Snap s)
        {
            // 失联最高优先级：此时快照里的一切都不可信，绝不能顺手渲染"还没有激活的配置"
            if (!s.Trusted)
            {
                Banner.Visibility = Visibility.Visible;
                BannerText.Foreground = (Brush)FindResource("Bad");
                BannerText.Text = "连不上面板后端（" + Api.BaseUrl + "）。下面的数据已停止更新。";
                BannerBtn.Visibility = Visibility.Collapsed;
                BannerRestart.Visibility = Visibility.Visible;
                return;
            }
            BannerRestart.Visibility = Visibility.Collapsed;

            // 端口回落：字段在就说明"这次进程的启动值被替换过"；字段消失即收起（不用计时器）
            if (s.Env.PortFallback != null)
            {
                Banner.Visibility = Visibility.Visible;
                BannerText.Foreground = (Brush)FindResource("Warn");
                BannerText.Text = "你填的面板端口是「" + s.Env.PortFallback.Value + "」，不是 1–65535 的整数，"
                                  + "已改用 " + s.Env.PortFallback.Used + "。";
                BannerBtn.Visibility = Visibility.Collapsed;
                return;
            }

            // 分流规则集没加载全：界面必须说，否则用户看到"规则在跑"其实是空的
            // 分流规则集：四态（backend 的 rulesOk = {ok,total,empty[],checkedAt}）
            //   null            → 不显示（内核没跑 / 还没查）
            //   total === 0     → 「查不到规则集」（**绝不渲染 0/0**）
            //   ok === true     → 不显示（正向信息不是必须）
            //   ok === false    → 「有 N/total 个没加载」
            if (s.Env.RulesOk != null && s.Env.RulesOkTotal == 0)
            {
                Banner.Visibility = Visibility.Visible;
                BannerText.Foreground = (Brush)FindResource("Warn");
                BannerText.Text = "查不到规则集，无法确认分流状态。";
                BannerBtn.Visibility = Visibility.Collapsed;
                return;
            }
            if (s.Env.RulesOk == false)
            {
                Banner.Visibility = Visibility.Visible;
                BannerText.Foreground = (Brush)FindResource("Bad");
                BannerText.Text = "分流规则有 " + s.Env.RulesOkFail + "/" + s.Env.RulesOkTotal
                                  + " 个没加载，这部分流量可能走错方向。";
                BannerBtn.Visibility = Visibility.Collapsed;
                return;
            }
            BannerText.Foreground = (Brush)FindResource("Warn");

            if (string.IsNullOrEmpty(s.ActiveProfile))
            {
                Banner.Visibility = Visibility.Visible;
                BannerText.Text = "还没有激活的配置。导入订阅或粘贴一份 YAML，激活后就能启动内核。";
                BannerBtn.Visibility = Visibility.Visible;
            }
            else if (s.Env.TunEnable && !s.Env.IsAdmin)
            {
                Banner.Visibility = Visibility.Visible;
                BannerText.Text = "TUN 已开启，但当前不是管理员权限，内核可能无法创建虚拟网卡。请用「以管理员身份运行」启动。";
                BannerBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                Banner.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateKv(Snap s)
        {
            FillKv(KvCore,
                ("状态", s.CoreRunning ? (string.IsNullOrEmpty(s.CoreVersion) ? "运行中" : "运行中 v" + s.CoreVersion) : "未启动"),
                ("运行时长", s.CoreRunning ? Fmt.Uptime(s.CoreUptime) : "—"),
                ("混合端口", s.MixedPort.ToString()),
                ("内核", string.IsNullOrEmpty(s.CorePath) ? "—" : System.IO.Path.GetFileName(s.CorePath)));

            var p = s.Active;
            FillKv(KvProfile,
                ("文件", p != null ? p.Name : "未选择"),
                ("剩余流量", p?.Quota != null ? Fmt.Bytes(p.Quota.Remain) + " / " + Fmt.Bytes(p.Quota.Total) : "—"),
                ("到期", p?.Quota != null && p.Quota.Expire > 0 ? Fmt.Expire(p.Quota.Expire) : "—"),
                ("节点数", s.Groups.Count > 0 ? s.Groups[0].All.Count + " 个" : "—"));
        }

        private void FillKv(Panel host, params (string k, string v)[] rows)
        {
            host.Children.Clear();
            foreach (var (k, v) in rows)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 7) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                g.Children.Add(new TextBlock
                {
                    Text = k,
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                }.Fg("Fg3"));

                var val = new TextBlock
                {
                    Text = v,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                }.Fg("Fg");
                Grid.SetColumn(val, 1);
                g.Children.Add(val);
                host.Children.Add(g);
            }
        }

        private void UpdateSwitches(Snap s)
        {
            UpdateSysProxy(s);
            SwTun.IsChecked = s.Env.TunEnable;
            SwEnhanced.IsChecked = s.Env.EnhancedRules;
            ApplyProxyModeUi(s.Env.ProxyMode);
            _lastInterval = s.Env.SubIntervalHours;
            if (!TxtSubInterval.IsFocused) TxtSubInterval.Text = _lastInterval.ToString();

            var mode = s.Mode ?? "";
            if (mode != _modeSig)
            {
                _modeSig = mode;
                ModeRule.IsChecked = mode == "rule" || mode == "";
                ModeGlobal.IsChecked = mode == "global";
                ModeDirect.IsChecked = mode == "direct";
            }
        }

        private void UpdateGroup(Snap s)
        {
            var top = s.Groups.Take(3).ToList();
            GroupTip.Text = top.Count > 0 ? $"{s.Groups.Count} 个策略组 · 点整行去「代理」页" : "";

            if (top.Count == 0)
            {
                if (GroupHost.Children.Count != 1)
                {
                    GroupHost.Children.Clear();
                    GroupHost.Children.Add(new TextBlock
                    {
                        Text = "启动内核后这里会显示策略组",
                        Style = St("Sub"),
                        Margin = new Thickness(0, 4, 0, 4),
                    });
                }
                return;
            }

            var sig = string.Join("|", top.Select(g => g.Name + "#" + g.Now + "#" + g.All.Count));
            if (sig == _groupSig) return;
            _groupSig = sig;

            GroupHost.Children.Clear();
            for (int i = 0; i < top.Count; i++)
            {
                GroupHost.Children.Add(BuildGroupRow(s, top[i], i == top.Count - 1));
            }
        }

        /// <summary>
        /// 概览只给一行摘要：组名 + 当前节点 + 测速。节点全表在「代理」页 ——
        /// 早先把节点列表塞进卡片里，被等高的兄弟卡片裁掉过，这种布局不会有这个问题。
        /// </summary>
        private Border BuildGroupRow(Snap s, GroupInfo g, bool last)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = g.Name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            }.Fg("Fg"));
            left.Children.Add(new Border
            {
                Style = St("Pill"),
                Background = Res("Panel3"),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = g.Type == "Selector" ? "手动" : g.Type == "URLTest" ? "自动" : g.Type,
                    FontSize = 9.5,
                }.Fg("Fg3"),
            });
            grid.Children.Add(left);

            var mid = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock
            {
                Text = Fmt.NormalizeFlags(g.Now),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 420,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = g.Now,
            }.Fg("Fg2"));

            var tier = Delay.Of(g.DelayOfNow);
            mid.Children.Add(new Border
            {
                Style = St("Pill"),
                Background = Res(Delay.SoftKey(tier)),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(9, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = Fmt.Delay(g.DelayOfNow),
                    FontSize = 10,
                }.Fg(Delay.BrushKey(tier)),
            });
            mid.Children.Add(new TextBlock
            {
                Text = $"   {g.All.Count} 个节点",
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
            }.Fg("Fg3"));
            Grid.SetColumn(mid, 1);
            grid.Children.Add(mid);

            var test = new Button
            {
                Content = "测速",
                Style = St("BtnSm"),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "测速地址：" + (s.Env.TestUrl ?? ""),
            };
            test.Click += async (_, __) =>
            {
                test.IsEnabled = false;
                test.Content = "测速中";
                var j = await Api.Get("/api/proxies/delay?group=" + Api.Q(g.Name) + "&timeout=" + s.Env.DelayTimeout, 120000);
                test.Content = "测速";
                test.IsEnabled = true;
                _owner.ShowToast(j.B("ok") ? $"{g.Name} 测速完成" : (j.S("msg") ?? "测速失败"), j.B("ok"));
            };
            Grid.SetColumn(test, 2);
            grid.Children.Add(test);

            var row = new Border
            {
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                BorderBrush = Res("Line"),
                BorderThickness = last ? new Thickness(0) : new Thickness(0, 0, 0, 1),
                Child = grid,
            };
            row.MouseEnter += (_, __) => row.Background = Res("Panel2");
            row.MouseLeave += (_, __) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, ev) =>
            {
                if (ev.OriginalSource is Button) return;
                _owner.GoPage("proxies");
            };
            return row;
        }

        // ============================================================ 曲线

        public override void OnHistory(JObj h)
        {
            var samples = h.List("samples");
            var pts = new List<double[]>(samples.Count);
            foreach (var x in samples) pts.Add(new[] { x.D("up"), x.D("down") });

            Chart.SetData(pts);

            if (pts.Count > 0)
            {
                var last = pts[pts.Count - 1];
                DownSpeed.Text = Fmt.Speed(last[1]);
                UpSpeed.Text = Fmt.Speed(last[0]);
            }
            ConnCount.Text = _lastConns.ToString();
        }

        private int _lastConns;

        // ============================================================ 流量统计

        private async Task LoadUsageAsync()
        {
            var u = await Api.Usage(30);
            if (!u.B("ok")) return;

            var today = u["today"];
            var month = u["month"];
            FillKv(KvUsage,
                ("今日下行", Fmt.Bytes(today.D("down"))),
                ("今日上行", Fmt.Bytes(today.D("up"))),
                ("本月合计", Fmt.Bytes(month.D("down") + month.D("up"))));

            var days = u.List("days");
            var tail = days.Skip(Math.Max(0, days.Count - 7)).ToList();
            UsageTip.Text = tail.Count > 0 ? "最近 " + tail.Count + " 天" : "暂无数据";

            UsageBars.Children.Clear();
            if (tail.Count == 0)
            {
                UsageBars.Children.Add(new TextBlock
                {
                    Text = "跑一会儿就有数据（每分钟记一笔）",
                    Style = St("Sub"),
                    VerticalAlignment = VerticalAlignment.Bottom,
                });
                return;
            }

            double max = Math.Max(1, tail.Max(d => d.D("down") + d.D("up")));
            foreach (var d in tail)
            {
                var total = d.D("down") + d.D("up");
                var col = new Grid { Margin = new Thickness(3, 0, 3, 0) };
                col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var bar = new Border
                {
                    Width = 15,
                    Height = Math.Max(3, 38 * total / max),
                    CornerRadius = new CornerRadius(3),
                    Background = Res("Accent"),
                    Opacity = 0.8,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    ToolTip = $"{d.S("day")}：↓ {Fmt.Bytes(d.D("down"))} · ↑ {Fmt.Bytes(d.D("up"))}",
                };
                col.Children.Add(bar);

                var lab = new TextBlock
                {
                    Text = d.S("day").Length >= 10 ? d.S("day").Substring(5) : d.S("day"),
                    FontSize = 9,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 3, 0, 0),
                }.Fg("Fg3");
                Grid.SetRow(lab, 1);
                col.Children.Add(lab);

                UsageBars.Children.Add(col);
            }
        }

        // ============================================================ 交互

        // ---- 系统代理：state 与 actionable 两个正交量（契约零-a）
        private bool _renderedChecked;   // 只承担一件事：记录"点击前的渲染值"，供取消时回滚
        private string _localOffNote;    // 只承担一件事：失联态点击的结果，留到下次可信快照
        private bool _lastTrusted;

        private static bool HasProxyTool => System.IO.File.Exists(Paths.SystemProxyTool);

        // 兼容 backend 尚未提供 env.sysProxy 的那一版：最多 2 秒取一次 /api/systemproxy
        private SysProxyInfo _siCompat;
        private DateTime _siAt = DateTime.MinValue;
        private bool _siBusy;

        private async void MaybeRefreshCompat()
        {
            if (_siBusy) return;
            if ((DateTime.UtcNow - _siAt).TotalSeconds < 2) return;
            _siBusy = true;
            _siAt = DateTime.UtcNow;
            try
            {
                var j = await Api.SysProxyStatus();
                var info = new SysProxyInfo();
                if (j.B("ok"))
                {
                    info.Enabled = j.B("enabled");
                    info.PointsToOurs = j.B("pointsToOurs");
                    info.Server = j.S("server");
                    info.Pac = j.S("pac");
                }
                _siCompat = info;
            }
            catch { /* 读不到就保持上一次的值 */ }
            finally { _siBusy = false; }
        }

        /// <summary>
        /// 9 态渲染。判据分两段：先是"前置失败"（失联 / 读不到），再是四个正交量的组合。
        /// <c>hasTool</c> 只影响可点性，不占状态位 —— 否则"工具缺失 + PAC 用户"会把 PAC 那句吃掉。
        /// </summary>
        private void UpdateSysProxy(Snap s)
        {
            var sp = s.Env.SysProxy;
            if (!sp.FromSnapshot && s.Trusted)
            {
                // 兼容路径：后端这一版还没把 sysProxy 放进快照时，退化成单独取一次
                // /api/systemproxy（最多 2 秒一次）。等 backend 落了 env.sysProxy 就自动不再触发。
                MaybeRefreshCompat();
                if (_siCompat != null) sp = _siCompat;
            }
            var hasTool = HasProxyTool;
            var enabled = sp.Enabled;

            string hint;
            string colorKey = "Fg3";
            bool showFix = false;
            bool needToolNote = false;

            if (!s.Trusted)
            {
                // 失联：读不到真值，不显示"开/关"这种断言
                SwSysProxy.IsChecked = false;
                hint = _localOffNote ?? "后端无响应，无法读取或更改系统代理";
                colorKey = _localOffNote != null ? "Fg3" : "Fg3";
            }
            else if (enabled == null)
            {
                // 有工具但读失败（被安全策略拦、文件被锁）也会落到这里 —— hasTool 只表示"文件在"，
                // 不表示"能读"。所以别把开关做成死的：让用户点，失败了把真实错误说出来。
                SwSysProxy.IsChecked = false;
                hint = hasTool ? "读不到系统代理状态（工具在，但执行失败）" : "连系统代理状态也读不到";
                showFix = true;
            }
            else if (!string.IsNullOrEmpty(sp.Pac))
            {
                // PAC 必须在 pointsToOurs 之前判：PAC 生效时 server 往往是空串
                SwSysProxy.IsChecked = enabled.Value;
                if (sp.PointsToOurs)
                {
                    hint = "代理服务器已指向本应用；另有 PAC 脚本 " + sp.Pac + " 同时配置，实际走哪条未确认";
                    colorKey = "Warn";
                }
                else if (!string.IsNullOrEmpty(sp.Server))
                {
                    hint = "代理服务器指向 " + sp.Server + "，另有 PAC 脚本 " + sp.Pac;
                    colorKey = "Warn";
                }
                else
                {
                    hint = "使用自动配置脚本 " + sp.Pac;
                    colorKey = "Warn";
                }
            }
            else if (!enabled.Value)
            {
                SwSysProxy.IsChecked = false;
                hint = "浏览器与桌面应用走代理";
            }
            else if (sp.PointsToOurs)
            {
                SwSysProxy.IsChecked = true;
                hint = "已开启，指向 127.0.0.1:" + s.MixedPort;
            }
            else
            {
                SwSysProxy.IsChecked = true;
                hint = "已开启，但指向 " + (string.IsNullOrEmpty(sp.Server) ? "(空)" : sp.Server) + "，不是本应用";
                colorKey = "Warn";
            }

            if (!hasTool)
            {
                needToolNote = true;
                showFix = true;
                if (s.Trusted && enabled == true) colorKey = "Bad";
            }

            // actionable：能不能点，与"是什么状态"完全分开。
            //
            // 这里**故意不含 `enabled == null`**：未知态下控件会画成"关"，
            // 用户以为自己读到了状态，一点就按"开启"方向写入 —— 而 A 批的 Enable()
            // 还没有备份（B8 才写），于是他会把别人原来的代理地址永久覆盖掉。
            // 未知态不是逃生路径（真正的逃生是 enabled==true && !coreRunning 的残留），
            // 所以这里必须禁用；有工具但读失败时旁边给「打开 Windows 代理设置」作为出路。
            bool actionable = hasTool && (s.Trusted
                ? (enabled == true || (enabled == false && s.CoreRunning))
                : true);

            SwSysProxy.IsEnabled = actionable;
            SwSysProxyHint.Text = hint + (needToolNote ? "（缺少 tools\\sysproxy.exe，无法更改）" : "");
            SwSysProxyHint.Foreground = (Brush)FindResource(colorKey);
            SwSysProxyFix.Visibility = showFix ? Visibility.Visible : Visibility.Collapsed;
            _renderedChecked = SwSysProxy.IsChecked == true;
        }

        private async void SwSysProxy_Click(object sender, RoutedEventArgs e)
        {
            if (_updating) return;
            // Click 发生在 IsChecked 翻转之后，这里拿到的是"用户想要的新值"
            var want = SwSysProxy.IsChecked == true;

            // ---- 失联态：POST 发不出去（后端已死），走壳直写；不谎报
            if (!_lastTrusted)
            {
                SwSysProxy.IsEnabled = false;
                var ok = SysProxy.DirectProxyOff(SysProxy.Caller.Click);
                _localOffNote = ok
                    ? "刚刚已关闭系统代理（后端无响应，状态未能复核）"
                    : "关闭失败：写注册表失败，代理可能仍在运行";
                SwSysProxy.IsChecked = ok ? false : _renderedChecked;
                SwSysProxy.IsEnabled = HasProxyTool;
                _owner.ShowToast(ok ? "系统代理已关闭" : "操作失败", ok);
                if (!ok) Paths.WriteLog(SysProxy.MOffFail + "sys 失联态本地关闭失败（写注册表报错）");
                return;
            }

            // ---- 关闭「别人的远程代理」前确认一次（回环死端口不确认：那是断网自救路径）
            var sp = _lastSnap == null ? null : _lastSnap.Env.SysProxy;
            if (!want && sp != null && sp.Enabled == true && !sp.PointsToOurs
                && !SysProxy.IsLoopbackAll(sp.Server))
            {
                var body = "系统代理当前指向 " + sp.Server + "，不是本应用设置的。\n"
                         + "关闭后你可能访问不了必须走这个代理的网络（例如公司内网）。\n"
                         + "原来的地址会保留，随时可以在 Windows 代理设置里重新开启。\n\n"
                         + "点「是」关闭系统代理；点「否」保持不动。";
                var yes = MessageBox.Show(body, "关闭系统代理？", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
                if (!yes)
                {
                    SwSysProxy.IsChecked = _renderedChecked;   // 立刻回滚，不等 2 秒快照
                    Paths.WriteLog(SysProxy.MForeignCancel + " via=click sys 用户在确认框取消，未做任何更改");
                    return;
                }
            }

            SwSysProxy.IsEnabled = false;
            var j = await Api.Post("/api/systemproxy", Json.Obj("enable", want), 15000);
            SwSysProxy.IsEnabled = HasProxyTool;
            if (!j.B("ok")) SwSysProxy.IsChecked = _renderedChecked;
            _owner.ShowToast(j.B("ok") ? (want ? "系统代理已开启" : "已关闭本应用代理")
                                       : (j.S("msg") ?? "操作失败"), j.B("ok"));
        }

        private void SwSysProxyFix_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start("ms-settings:network-proxy"); }
            catch (Exception ex) { _owner.ShowToast("打不开系统设置：" + ex.Message, false); }
        }

        private async void BannerRestart_Click(object sender, RoutedEventArgs e)
        {
            BannerRestart.IsEnabled = false;
            try { await _owner.RestartBackendAsync(); }
            finally { BannerRestart.IsEnabled = true; }
        }

        private async void SwTun_Click(object sender, RoutedEventArgs e)
        {
            if (_updating) return;
            var want = SwTun.IsChecked == true;
            var j = await Api.Post("/api/settings", Json.Obj("tunEnable", want));
            if (!j.B("ok"))
            {
                SwTun.IsChecked = !want;
                _owner.ShowToast(j.S("msg") ?? "设置失败", false);
                return;
            }
            _owner.ShowToast(want ? "TUN 已开启，重启内核后生效" : "TUN 已关闭，重启内核后生效", true);
        }



        // ---- 应用内更新：检查 → 下载校验 → 静默安装并重启 ----

        private void ShowUpdateChecked(JObj j)
        {
            if (!j.B("ok"))
            {
                UpdStatus.Text = j.S("msg") ?? "检查更新失败";
                UpdActions.Visibility = Visibility.Collapsed;
                return;
            }
            var cur = j.S("current") ?? "";
            UpdCurrent.Text = "当前版本 v" + cur;
            if (j.B("hasUpdate"))
            {
                var size = j.D("size") / 1048576.0;
                UpdStatus.Text = string.Format("发现新版本 v{0}（{1:0.0} MB）。点下面开始下载，校验通过后会自动重启安装。",
                    j.S("latest"), size);
                BtnDoUpdate.Content = "下载并安装 v" + j.S("latest");
                UpdActions.Visibility = Visibility.Visible;
            }
            else if (j.B("behind"))
            {
                UpdStatus.Text = "本地版本（" + cur + "）比线上还新，不降级。";
                UpdActions.Visibility = Visibility.Collapsed;
            }
            else
            {
                UpdStatus.Text = "已是最新版本 v" + cur + "。";
                UpdActions.Visibility = Visibility.Collapsed;
            }
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            BtnCheckUpdate.IsEnabled = false;
            UpdStatus.Text = "正在连更新服务器…";
            try
            {
                var j = await Api.Get("/api/update/check", 15000);
                ShowUpdateChecked(j);
            }
            catch (Exception ex) { UpdStatus.Text = "检查更新失败：" + ex.Message; }
            finally { BtnCheckUpdate.IsEnabled = true; }
        }

        private async void DoUpdate_Click(object sender, RoutedEventArgs e)
        {
            BtnDoUpdate.IsEnabled = false;
            BtnCheckUpdate.IsEnabled = false;
            UpdStatus.Text = "正在下载…（文件约 70 MB，看网速可能要一会儿）";

            // 下载期间轮询进度；失败/成功都要停掉
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            timer.Tick += async (s2, e2) =>
            {
                try
                {
                    var p = await Api.Get("/api/update/progress", 4000);
                    var prog = p["progress"];
                    if (prog != null && prog is JObj po)
                    {
                        var pct = po.I("pct");
                        var got = po.D("got") / 1048576.0;
                        UpdStatus.Text = string.Format("正在下载… {0}%（{1:0.0} MB）", pct, got);
                    }
                }
                catch { /* 进度查不到不影响下载 */ }
            };
            timer.Start();
            try
            {
                var j = await Api.Post("/api/update/download", null, 600000);
                timer.Stop();
                if (!j.B("ok"))
                {
                    UpdStatus.Text = j.S("msg") ?? "下载失败";
                    BtnDoUpdate.IsEnabled = true;
                    BtnCheckUpdate.IsEnabled = true;
                    return;
                }
                UpdStatus.Text = "下载完成，校验通过。现在会重启并安装 v" + j.S("version") + "，装完自动打开。";
                BtnDoUpdate.Content = "正在更新…";
                var a = await Api.Post("/api/update/apply", null, 15000);
                if (!a.B("ok"))
                {
                    UpdStatus.Text = a.S("msg") ?? "启动安装失败";
                    BtnDoUpdate.IsEnabled = true;
                    BtnCheckUpdate.IsEnabled = true;
                    return;
                }
                // 立刻退出，把文件让给安装器（批处理里还有兜底 taskkill）
                await Task.Delay(1200);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                timer.Stop();
                UpdStatus.Text = "更新失败：" + ex.Message;
                BtnDoUpdate.IsEnabled = true;
                BtnCheckUpdate.IsEnabled = true;
            }
        }

        // ---- 带宽测速（结果只在界面上显示，后端不落盘）----
        private async void SpeedDown_Click(object sender, RoutedEventArgs e) { await RunSpeedAsync("down"); }
        private async void SpeedUp_Click(object sender, RoutedEventArgs e) { await RunSpeedAsync("up"); }

        private async Task RunSpeedAsync(string mode)
        {
            BtnDown.IsEnabled = false; BtnUp.IsEnabled = false;
            SpeedResult.Text = mode == "down" ? "正在测下行…（最多 60 秒）" : "正在测上行…（会先写一块临时数据，测完立即删除）";
            try
            {
                var j = await Api.Get("/api/speedtest?mode=" + mode, 90000);
                if (j.B("ok"))
                {
                    var label = mode == "down" ? "下行" : "上行";
                    SpeedResult.Text = string.Format("{0} {1:0.00} MB/s（{2:0.0} Mbps） · 传输 {3:0.0} MB · 用时 {4:0.00} 秒",
                        label, j.D("MBps"), j.D("Mbps"), j.D("bytes") / 1048576.0, j.D("seconds"));
                }
                else SpeedResult.Text = j.S("msg") ?? "测速失败";
            }
            catch (Exception ex) { SpeedResult.Text = "测速失败：" + ex.Message; }
            finally { BtnDown.IsEnabled = true; BtnUp.IsEnabled = true; }
        }

        // ---- 订阅自动更新间隔 ----
        private int _lastInterval = 24;

        private async void SubInterval_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_updating) return;
            var txt = (TxtSubInterval.Text ?? "").Trim();
            int h;
            if (!int.TryParse(txt, out h) || h < 0 || h > 168)
            {
                _owner.ShowToast("间隔需在 0–168 之间（0 = 不自动更新）", false);
                TxtSubInterval.Text = _lastInterval.ToString();
                return;
            }
            if (h == _lastInterval) return;
            var j = await Api.Post("/api/settings", Json.Obj("subIntervalHours", h));
            if (!j.B("ok")) { _owner.ShowToast(j.S("msg") ?? "设置失败", false); TxtSubInterval.Text = _lastInterval.ToString(); return; }
            _lastInterval = h;
            _owner.ShowToast(h == 0 ? "已关闭订阅自动更新" : "订阅每 " + h + " 小时自动更新", true);
        }

        private string _curProxyMode = "smart";
        private bool _modeBusy;

        /// <summary>
        /// 这里刻意**不用** RadioButton / CheckBox 来表示"当前模式"。
        /// WPF 的 RadioButton 一旦 IsChecked 被手动或点击改过，就与组内其它按钮的产生联动副作用；
        /// 而界面每秒刷新的快照又会重设选中态 —— 两个机制打架，"切完又弹回"就是这么来的。
        /// 改成：两个无状态的普通按钮 + 一行文字显示当前值，界面只"显示"不"持有"状态。
        /// </summary>
        private void ApplyProxyModeUi(string mode)
        {
            _curProxyMode = mode == "whitelist" ? "whitelist" : "smart";
            var strict = _curProxyMode == "whitelist";
            ModeHint.Text = strict
                ? "当前：严格 —— 内置国外域名名单内走代理，名单外直连。"
                : "当前：智能 —— 名单内走代理，名单外也走代理（兼容性最好）。";
            // 选中视觉挂在 Tag 上（只由数据写入），不用控件的选中态
            SegSmart.Tag = strict ? null : "on";
            SegStrict.Tag = strict ? "on" : null;
        }

        private async void SetSmart_Click(object sender, RoutedEventArgs e) => await SetModeAsync("smart");
        private async void SetStrict_Click(object sender, RoutedEventArgs e) => await SetModeAsync("whitelist");

        private async Task SetModeAsync(string mode)
        {
            if (_modeBusy) return;          // 防连点：每次切换都要重启内核，连点会连续重启
            _modeBusy = true;
            SegSmart.IsEnabled = false;
            SegStrict.IsEnabled = false;
            try
            {
                _owner.ShowToast("正在切换到" + (mode == "whitelist" ? "严格" : "智能") + "…", true);
                try { await Api.Post("/api/log", Json.Obj("text", "点击切换分流模式 → " + mode + "（当前 " + _curProxyMode + "）")); } catch { }

                var j = await Api.Post("/api/settings", Json.Obj("proxyMode", mode));
                if (!j.B("ok")) { _owner.ShowToast(j.S("msg") ?? "切换失败", false); return; }

                ApplyProxyModeUi(mode);
                var r = await Api.Post("/api/core/restart", null, 90000);
                _owner.ShowToast(r.B("ok") ? "已切换到" + (mode == "whitelist" ? "严格" : "智能") + "，内核已重启"
                                           : "已保存，但内核重启失败：" + (r.S("msg") ?? ""), r.B("ok"));
            }
            catch (Exception ex) { _owner.ShowToast("切换失败：" + ex.Message, false); }
            finally
            {
                SegSmart.IsEnabled = true;
                SegStrict.IsEnabled = true;
                _modeBusy = false;
            }
        }

        private async void SwEnhanced_Click(object sender, RoutedEventArgs e)
        {
            if (_updating) return;
            var want = SwEnhanced.IsChecked == true;
            var j = await Api.Post("/api/settings", Json.Obj("enhancedRules", want));
            if (!j.B("ok"))
            {
                SwEnhanced.IsChecked = !want;
                _owner.ShowToast(j.S("msg") ?? "设置失败", false);
                return;
            }
            _owner.ShowToast(want ? "增强分流已开启，重启内核后生效" : "已改用订阅自带规则，重启内核后生效", true);
        }

        private async void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (_updating || !IsLoaded) return;
            string mode = ModeGlobal.IsChecked == true ? "global"
                : ModeDirect.IsChecked == true ? "direct" : "rule";

            var j = await Api.Post("/api/mode", Json.Obj("mode", mode));
            if (!j.B("ok")) _owner.ShowToast(j.S("msg") ?? "切换失败", false);
        }

        private void BannerBtn_Click(object sender, RoutedEventArgs e) => _owner.GoPage("profiles");
    }
}
