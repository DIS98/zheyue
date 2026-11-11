using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ClashDeck.Core;

namespace ClashDeck.Views
{
    public partial class LogsView : PageBase
    {
        private sealed class LogLine
        {
            public string T, Stream, Text;
        }

        private readonly MainWindow _owner;
        private readonly System.Windows.Threading.DispatcherTimer _timer;
        private readonly List<LogLine> _all = new List<LogLine>();

        /// <summary>后端分配的单调序号（v=2 协议的游标），不是缓冲数组下标。</summary>
        private long _cursor;
        /// <summary>累计因缓冲回绕而永久丢掉的旧行数。只用于如实说明，不拦滚动。</summary>
        private long _dropped;
        /// <summary>页面窗口：只留最近这么多行，更早的直接不显示。</summary>
        private int _window = 2000;
        private bool _paused;
        private bool _busy;
        private bool _syncing;
        private bool _envSynced;

        public LogsView(MainWindow owner)
        {
            _owner = owner;
            InitializeComponent();
            LogBox.Document.Blocks.Clear();
            _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += async (_, __) => { if (IsVisible) await PollAsync(); };
            _timer.Start();
        }

        public override async void OnActivated() => await PollAsync();

        public override void OnSnap(Snap s) { }

        private async System.Threading.Tasks.Task SyncAsync()
        {
            if (_envSynced) return;
            try
            {
                var e2 = await Api.Env();
                _syncing = true;
                SwLogConn.IsChecked = e2.B("logConn");
                SelectByTag(Retention, e2.L("logKeepDays"));
                _envSynced = true;
            }
            catch { /* 拿不到就下次再试 */ }
            finally { _syncing = false; }   // 异常时也要放开，否则下拉框永久失联
        }

        private static void SelectByTag(ComboBox box, long value)
        {
            foreach (var o in box.Items)
                if (o is ComboBoxItem it && it.Tag is string t && long.TryParse(t, out long v) && v == value)
                {
                    box.SelectedItem = it;
                    return;
                }
        }

        private long TagOf(ComboBox box)
        {
            if (box.SelectedItem is ComboBoxItem it && it.Tag is string t && long.TryParse(t, out long v)) return v;
            return 0;
        }

        /// <summary>
        /// 拉一次增量。
        ///
        /// 关键点只有一个：游标用服务端的 `next`（单调序号）。老实现把 `total` 当游标，
        /// 而 total 是缓冲长度、饱和在 600 —— 一旦吃满就再也拿不到新行，
        /// 页面上看到的就是"到了几百行就停住不动"。
        /// </summary>
        private async System.Threading.Tasks.Task PollAsync()
        {
            await SyncAsync();
            if (_busy) return;
            _busy = true;
            try
            {
                var j = await Api.Logs(_cursor);
                if (!j.B("ok")) return;

                if (j.B("reset"))
                {
                    // 后端重启过：序号空间回到 0，手里这批已经对不上，清掉重铺
                    _all.Clear();
                    _cursor = 0;
                    _dropped = 0;
                    LogBox.Document.Blocks.Clear();
                }

                var lines = j.List("lines");
                if (lines.Count > 0)
                {
                    var added = new List<LogLine>(lines.Count);
                    foreach (var l in lines)
                    {
                        var ll = new LogLine
                        {
                            T = l.S("t"),
                            Stream = l.S("stream"),
                            Text = l.S("line"),
                        };
                        if (string.IsNullOrEmpty(ll.T)) ll.T = Fmt.Clock();
                        _all.Add(ll);
                        added.Add(ll);
                    }

                    bool overflow = _all.Count > _window;
                    if (overflow) _all.RemoveRange(0, _all.Count - _window);

                    // 只有"首次铺满"和"带过滤条件"才整块重绘：
                    // 稳态下每 2 秒重绘 2000 段会把界面拖卡，增量追加 + 掐头就够。
                    RefreshFilter();
                    if (Filtering() || added.Count > _window) Rebuild();
                    else AppendRendered(added);
                }

                var next = j.L("next");
                if (next > 0) _cursor = next;
                _dropped += j.L("dropped");

                UpdateTip();
            }
            finally { _busy = false; }
        }

        private void UpdateTip()
        {
            // 一行说完：现在有多少行、窗口上限多少。
            // 「其中 N 行已滚出窗口」这种长句会把右上角的按钮挤走，需要细节就放在提示里。
            Tip.Text = _all.Count + " 行 · 窗口 " + _window;
            Tip.ToolTip = _dropped > 0
                ? "更早的 " + _dropped + " 行已随窗口滚动丢弃；调大「显示行数」可以多看一些，磁盘日志不受影响。"
                : "页面只保留最近 " + _window + " 行，更早的自动滚出去；磁盘日志不受影响。";
        }

        /// <summary>当前过滤用的关键字（小写）。每次重绘前刷新一次，不逐行 split。</summary>
        private string[] _kws = new string[0];

        private void RefreshFilter() => _kws = Keywords();

        private bool Filtering() => _kws.Length > 0 || LevelTag() != "all";

        /// <summary>
        /// 一次取齐渲染要用的画刷。
        ///
        /// 以前每行每段都 TryFindResource 三次：5000 行窗口 = 1.5 万次资源链查找，
        /// 而这几支笔在一次重绘里是常量。主题切换时会整页重建，所以缓存一次不会串色。
        /// </summary>
        private struct LogBrushes
        {
            public Brush Fg2, Fg3, Accent, Bad, Warn;
        }

        private LogBrushes TakeBrushes() => new LogBrushes
        {
            Fg2 = Res("Fg2"),
            Fg3 = Res("Fg3"),
            Accent = Res("Accent"),
            Bad = Res("Bad"),
            Warn = Res("Warn"),
        };

        // ============================================================ 渲染

        private string LevelTag()
        {
            if (Level.SelectedItem is ComboBoxItem it && it.Tag is string t) return t;
            return "all";
        }

        private string[] Keywords()
        {
            var raw = (Search.Text ?? "").Trim().ToLowerInvariant();
            return raw.Length == 0 ? new string[0] : raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private bool Match(LogLine l)
        {
            var lv = LevelTag();
            if (lv == "sys" && l.Stream != "sys") return false;
            if (lv == "conn" && l.Stream != "conn") return false;
            if (lv == "core" && l.Stream != "core" && l.Stream != "out") return false;
            if (lv == "info" && l.Text.IndexOf("level=info", StringComparison.Ordinal) < 0) return false;
            if (lv == "warn" &&
                l.Text.IndexOf("level=warn", StringComparison.Ordinal) < 0 &&
                l.Text.IndexOf("level=error", StringComparison.Ordinal) < 0) return false;

            if (_kws.Length > 0)
            {
                var low = l.Text.ToLowerInvariant();
                foreach (var k in _kws) if (low.IndexOf(k, StringComparison.Ordinal) < 0) return false;
            }
            return true;
        }

        private void AppendRendered(List<LogLine> added)
        {
            // 只有"不过滤"的路径才会走这里（过滤态一律 Rebuild），所以不必逐行 Match
            var br = TakeBrushes();
            foreach (var l in added) LogBox.Document.Blocks.Add(BuildPara(l, br));
            while (LogBox.Document.Blocks.Count > _window)
            {
                var first = LogBox.Document.Blocks.FirstBlock;
                if (first == null) break;
                LogBox.Document.Blocks.Remove(first);
            }
            if (!_paused) LogBox.ScrollToEnd();
        }

        private void Rebuild()
        {
            RefreshFilter();
            var br = TakeBrushes();
            LogBox.Document.Blocks.Clear();
            foreach (var l in _all)
            {
                if (!Match(l)) continue;
                LogBox.Document.Blocks.Add(BuildPara(l, br));
            }
            if (!_paused) LogBox.ScrollToEnd();
        }

        private Paragraph BuildPara(LogLine l, LogBrushes br)
        {
            if (LogBox.Document.PageWidth < 80) SyncPageWidth();
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 1) };

            p.Inlines.Add(new Run(l.T) { Foreground = br.Fg3 });
            p.Inlines.Add(new Run("  "));

            var fg = br.Fg2;
            if (l.Stream == "sys") fg = br.Accent;
            else if (l.Stream == "err" || l.Text.IndexOf("level=error", StringComparison.Ordinal) >= 0) fg = br.Bad;
            else if (l.Text.IndexOf("level=warn", StringComparison.Ordinal) >= 0) fg = br.Warn;

            p.Inlines.Add(new Run(l.Text) { Foreground = fg });
            return p;
        }

        /// <summary>
        /// RichTextBox 的换行由 FlowDocument.PageWidth 决定，不是 TextWrapping。
        /// 不跟着控件宽度走，长行（比如带节点名的连接记录）会横向溢出；
        /// 而横向滚动条是关的，右边就被直接裁掉了。
        /// </summary>
        private void LogBox_SizeChanged(object sender, SizeChangedEventArgs e) => SyncPageWidth();

        private void SyncPageWidth()
        {
            if (LogBox?.Document == null) return;
            var w = LogBox.ActualWidth - LogBox.Padding.Left - LogBox.Padding.Right - 6;
            if (w > 80) LogBox.Document.PageWidth = w;
        }

        private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

        // ============================================================ 交互

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchHint != null)
                SearchHint.Visibility = string.IsNullOrEmpty(Search.Text) ? Visibility.Visible : Visibility.Collapsed;
            if (!IsLoaded) return;
            Rebuild();
        }

        private void WindowBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            var n = (int)TagOf(WindowBox);
            if (n < 100) return;
            _window = n;
            if (_all.Count > _window) _all.RemoveRange(0, _all.Count - _window);
            Rebuild();
            UpdateTip();
        }

        private async void Retention_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || !IsLoaded) return;
            var days = TagOf(Retention);
            var j = await Api.Post("/api/settings", Json.Obj("logKeepDays", days));
            if (j.B("ok"))
                _owner.ShowToast(days == 0 ? "日志不再自动删除" : "日志只保留最近 " + days + " 天", true);
            else
            {
                _owner.ShowToast(j.S("msg") ?? "设置失败", false);
                _syncing = true;
                SelectByTag(Retention, days == 0 ? 7 : 0);
                _syncing = false;
            }
        }

        private async void LogConn_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            var on = SwLogConn.IsChecked == true;
            var j = await Api.Post("/api/settings", Json.Obj("logConn", on));
            if (j.B("ok")) _owner.ShowToast(on ? "已开始记录连接日志" : "已停止记录连接日志（已有的不受影响）", true);
            else
            {
                _owner.ShowToast(j.S("msg") ?? "设置失败", false);
                _syncing = true; SwLogConn.IsChecked = !on; _syncing = false;
            }
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            BtnExport.IsEnabled = false;
            try
            {
                var stream = LevelTag();
                var j = await Api.Get("/api/logs/export?stream=" + Uri.EscapeDataString(stream), 20000);
                if (!j.B("ok")) { _owner.ShowToast(j.S("msg") ?? "导出失败", false); return; }
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = "折跃日志-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt",
                    Filter = "文本文件|*.txt|所有文件|*.*",
                    Title = "保存日志（已脱敏）",
                };
                if (dlg.ShowDialog() != true) return;
                System.IO.File.WriteAllText(dlg.FileName, j.S("text") ?? "", new System.Text.UTF8Encoding(false));
                _owner.ShowToast("已导出 " + j.I("count") + " 行", true);
            }
            catch (Exception ex) { _owner.ShowToast("导出失败：" + ex.Message, false); }
            finally { BtnExport.IsEnabled = true; }
        }

        private async void OpenLogDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var j = await Api.Get("/api/logs/export?stream=all", 15000);
                var dir = j.S("dir");
                if (string.IsNullOrEmpty(dir)) { _owner.ShowToast("拿不到日志目录", false); return; }
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { _owner.ShowToast("打开目录失败：" + ex.Message, false); }
        }

        private void Level_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            Rebuild();
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            _paused = !_paused;
            BtnPause.Content = _paused ? "继续滚动" : "暂停滚动";
            if (!_paused) LogBox.ScrollToEnd();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _all.Clear();
            LogBox.Document.Blocks.Clear();
            _dropped = 0;
            Tip.Text = "0 行";
        }
    }
}
