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
        private long _cursor;
        private bool _paused;
        private bool _busy;
        private const int MaxLines = 900;

        /// <summary>
        /// 后端 core.log 的环形缓冲上限（server.js 的 core.logLimit）。
        ///
        /// 这里复制一份是**临时的**：只用于"冻结止血提示"的判断，因为客户端没有别的手段
        /// 区分"缓冲满了卡死"和"应用闲置没有新日志"（两者的 (lines=0 && total==cursor) 完全一样）。
        /// B0 把游标改成 seq/next 之后，这个常量连同上面那段提示一起删掉。
        /// 改 server.js 的 logLimit 时，这个数要跟着改。
        /// </summary>
        private const int ServerBufferCap = 600;

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

        private bool _envSynced;

        private async System.Threading.Tasks.Task SyncSwitchAsync()
        {
            if (_envSynced) return;
            try
            {
                var e2 = await Api.Env();
                _syncing = true;
                SwLogConn.IsChecked = e2.B("logConn");
                _syncing = false;
                _envSynced = true;
            }
            catch { /* 拿不到就下次再试 */ }
        }

        private async System.Threading.Tasks.Task PollAsync()
        {
            await SyncSwitchAsync();
            if (_busy) return;
            _busy = true;
            try
            {
                var j = await Api.Logs(_cursor);
                if (!j.B("ok")) return;

                var total = j.L("total");
                if (total < _cursor) { _cursor = 0; _all.Clear(); Rebuild(); }

                var lines = j.List("lines");
                if (lines.Count > 0)
                {
                    _cursor = total;
                    var added = new List<LogLine>();
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
                    Trim();
                    AppendRendered(added);
                }

                // ---------------------------------------------------------------- 止血提示
                // 已知缺陷（B0 修）：core.log 是 600 行环形缓冲，total 饱和在 600；
                // 而游标只在"有返回行"时推进 —— 一旦吃到 600，slice(600) 恒空，页面永久停更，
                // 而且不报错（Tip 继续显示 N 行），用户会以为"没日志了"。
                //
                // 这一行不修 bug，只把"静默冻结"变成"看得见的故障"，并指一条真出路：
                // 落盘文件走的是另一条路径（logToFile），不受这个游标影响，所以用户想拿日志
                // 根本不需要重启。
                //
                // 判据必须带 total >= ServerBufferCap：否则"应用闲置、没有新日志"也会满足
                // (lines=0 && total==_cursor)，那是正常状态，不能报警。
                if (lines.Count == 0 && total == _cursor && total >= ServerBufferCap)
                {
                    // 这条不修 bug，只把"静默冻结"变成"看得见的故障"，并指一条真出路：
                    // 落盘日志走的是另一条路径（logToFile），不受这个游标影响，所以用户
                    // 想拿日志根本不需要重启应用 —— B0 修好之前，这是唯一的自救路径。
                    Tip.Text = $"{_all.Count} 行 · 已停止更新";
                    // 措辞有两处不能写：
                    //  ① 不能写"完整记录" —— 磁盘日志默认只保留最近 7 天（pruneLogs），
                    //     且落盘那份是**脱敏后**的（订阅查询串/token/长哈希打码）。
                    //     连接记录本身（域名/命中规则/节点名）不打码，所以"去文件里查连接"仍然成立。
                    //  ② 不能写"重启应用可恢复" —— 重启后能再吃 600 行又冻，那是"暂时好了"，
                    //     会让用户以为已经修好。
                    FreezeTipText.Text = $"日志显示已停止更新（缓冲已满 {ServerBufferCap} 行）。"
                                       + "点右上角的「日志文件」可查看磁盘日志（默认保留最近 7 天）—— "
                                       + "落盘不受这个问题影响。";
                    FreezeTip.Visibility = Visibility.Visible;
                }
                else
                {
                    Tip.Text = $"{_all.Count} 行";
                    FreezeTip.Visibility = Visibility.Collapsed;
                }
            }
            finally { _busy = false; }
        }

        private void Trim()
        {
            if (_all.Count <= MaxLines) return;
            var drop = _all.Count - MaxLines;
            _all.RemoveRange(0, drop);
            Rebuild();
        }

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
            if (lv == "core" && l.Stream != "core") return false;
            if (lv == "info" && l.Text.IndexOf("level=info", StringComparison.Ordinal) < 0) return false;
            if (lv == "warn" &&
                l.Text.IndexOf("level=warn", StringComparison.Ordinal) < 0 &&
                l.Text.IndexOf("level=error", StringComparison.Ordinal) < 0) return false;

            var kws = Keywords();
            if (kws.Length > 0)
            {
                var low = l.Text.ToLowerInvariant();
                foreach (var k in kws) if (low.IndexOf(k, StringComparison.Ordinal) < 0) return false;
            }
            return true;
        }

        private void AppendRendered(List<LogLine> added)
        {
            bool filtering = Keywords().Length > 0 || LevelTag() != "all";
            if (filtering) { Rebuild(); return; }

            foreach (var l in added)
            {
                if (!Match(l)) continue;
                LogBox.Document.Blocks.Add(BuildPara(l));
            }
            while (LogBox.Document.Blocks.Count > MaxLines)
            {
                var first = LogBox.Document.Blocks.FirstBlock;
                if (first == null) break;
                LogBox.Document.Blocks.Remove(first);
            }
            if (!_paused) LogBox.ScrollToEnd();
        }

        private void Rebuild()
        {
            LogBox.Document.Blocks.Clear();
            foreach (var l in _all)
            {
                if (!Match(l)) continue;
                LogBox.Document.Blocks.Add(BuildPara(l));
            }
            if (!_paused) LogBox.ScrollToEnd();
        }

        private Paragraph BuildPara(LogLine l)
        {
            if (LogBox.Document.PageWidth < 80) SyncPageWidth();
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 1) };

            p.Inlines.Add(new Run(l.T) { Foreground = Res("Fg3") });
            p.Inlines.Add(new Run("  "));

            var fg = Res("Fg2");
            if (l.Stream == "sys") fg = Res("Accent");
            else if (l.Stream == "err" || l.Text.IndexOf("level=error", StringComparison.Ordinal) >= 0) fg = Res("Bad");
            else if (l.Text.IndexOf("level=warn", StringComparison.Ordinal) >= 0) fg = Res("Warn");

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

        private bool _syncing;

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
            Tip.Text = "0 行";
        }
    }
}
