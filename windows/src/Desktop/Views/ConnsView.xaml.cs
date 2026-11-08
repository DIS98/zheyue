using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClashDeck.Core;

namespace ClashDeck.Views
{
    public sealed class ConnVm
    {
        public string Id { get; set; } = "";
        public string Host { get; set; } = "";
        public string Dest { get; set; } = "";
        public string Source { get; set; } = "";
        public string Net { get; set; } = "";
        public string Type { get; set; } = "";
        public string RuleText { get; set; } = "";
        public string RuleRaw { get; set; } = "";
        public string RulePayload { get; set; } = "";
        public string Chains { get; set; } = "";
        public string Process { get; set; } = "";
        public string ProcessName { get; set; } = "—";
        public string Up { get; set; } = "";
        public string Down { get; set; } = "";
        public long UpBytes, DownBytes;
        public double Duration;
        public bool IsSelected;

        public static ConnVm From(JObj c)
        {
            var chains = c.StrList("chains");
            var proc = c.S("process");
            var name = "";
            if (!string.IsNullOrEmpty(proc))
            {
                var parts = proc.Split('\\', '/');
                name = parts.Length > 0 ? parts[parts.Length - 1] : proc;
            }
            var rule = c.S("rule");
            var payload = c.S("rulePayload");
            return new ConnVm
            {
                Id = c.S("id"),
                Host = c.S("host"),
                Dest = c.S("dest"),
                Source = c.S("source"),
                Net = c.S("net"),
                Type = c.S("type"),
                RuleRaw = rule,
                RulePayload = payload,
                RuleText = string.IsNullOrEmpty(payload) ? rule : rule + "(" + payload + ")",
                Chains = string.Join(" ← ", chains.Where(x => !string.IsNullOrEmpty(x)).Select(Fmt.NormalizeFlags)),
                Process = proc,
                ProcessName = string.IsNullOrEmpty(name) ? "—" : name,
                UpBytes = (long)c.D("upload"),
                DownBytes = (long)c.D("download"),
                Up = Fmt.Bytes(c.D("upload")),
                Down = Fmt.Bytes(c.D("download")),
                Duration = c.D("duration"),
            };
        }
    }

    public sealed class ProcVm
    {
        public string Name { get; set; } = "";
        public string Count { get; set; } = "";
        public string Up { get; set; } = "";
        public string Down { get; set; } = "";
        public string Chains { get; set; } = "";
        public int N;
        public long UpBytes, DownBytes;
        public HashSet<string> ChainSet { get; set; } = new HashSet<string>();
    }

    public partial class ConnsView : PageBase
    {
        private readonly MainWindow _owner;
        private readonly System.Windows.Threading.DispatcherTimer _timer;
        private List<ConnVm> _all = new List<ConnVm>();
        private string _selectedId;
        private bool _busy;
        private int _view = 0; // 0=单条 1=按进程

        public ConnsView(MainWindow owner)
        {
            _owner = owner;
            InitializeComponent();
            _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += async (_, __) => { if (IsVisible) await LoadAsync(); };
            _timer.Start();
        }

        public override async void OnActivated() => await LoadAsync();

        public override void OnSnap(Snap s) { /* 连接明细自己拉，不依赖快照 */ }

        private async Task LoadAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                var j = await Api.Connections();
                if (!j.B("ok"))
                {
                    Tip.Text = j.S("msg");
                    return;
                }

                _all = j.List("list").Select(ConnVm.From).ToList();
                var total = j.I("total");
                var kws = Keywords();
                var shown = kws.Length == 0
                    ? _all
                    : _all.Where(c => kws.All(k =>
                        (c.Host + " " + c.ProcessName + " " + c.Chains + " " + c.RuleText)
                        .ToLowerInvariant().Contains(k))).ToList();

                Tip.Text = $"共 {total} 条" + (kws.Length > 0 ? $"，匹配 {shown.Count}" : "");

                foreach (var c in shown) c.IsSelected = c.Id == _selectedId;

                if (_view == 0) RenderList(shown);
                else RenderProc(shown);

                if (shown.Count == 0) EmptyHint.Visibility = Visibility.Visible;
                else EmptyHint.Visibility = Visibility.Collapsed;

                // 没有连接时"全部断开"点了也不会发生任何事，按钮却一直是可点的红色，
                // 看着像有东西可断。按实际条数启用/禁用。
                CloseAllBtn.IsEnabled = shown.Count > 0;
            }
            finally { _busy = false; }
        }

        private void RenderList(List<ConnVm> list)
        {
            ConnList.ItemsSource = list;
            ConnList.Visibility = Visibility.Visible;
            ProcScroll.Visibility = Visibility.Collapsed;
        }

        private void RenderProc(List<ConnVm> list)
        {
            var map = new Dictionary<string, ProcVm>(StringComparer.Ordinal);
            foreach (var c in list)
            {
                if (!map.TryGetValue(c.ProcessName, out var p))
                {
                    p = new ProcVm { Name = c.ProcessName };
                    map[c.ProcessName] = p;
                }
                p.UpBytes += c.UpBytes;
                p.DownBytes += c.DownBytes;
                p.N++;
                if (p.ChainSet == null) p.ChainSet = new HashSet<string>();
                if (!string.IsNullOrEmpty(c.Chains)) p.ChainSet.Add(c.Chains);
            }

            var rows = map.Values.OrderByDescending(p => p.UpBytes + p.DownBytes).ToList();
            foreach (var p in rows)
            {
                p.Count = p.N.ToString();
                p.Up = Fmt.Bytes(p.UpBytes);
                p.Down = Fmt.Bytes(p.DownBytes);
                p.Chains = string.Join(" · ", p.ChainSet.Take(2).Select(Fmt.NormalizeFlags));
            }

            ProcList.ItemsSource = rows;
            ConnList.Visibility = Visibility.Collapsed;
            ProcScroll.Visibility = Visibility.Visible;
        }

        private string[] Keywords()
        {
            var raw = (Search.Text ?? "").Trim().ToLowerInvariant();
            return raw.Length == 0 ? new string[0] : raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // ============================================================ 交互

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchHint != null)
                SearchHint.Visibility = string.IsNullOrEmpty(Search.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void View_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _view = ViewProc.IsChecked == true ? 1 : 0;
            _ = LoadAsync();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

        private async void CloseAll_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("断开全部连接？已有下载会中断。", "折跃",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var j = await Api.Delete("/api/connections");
            _owner.ShowToast(j.B("ok") ? "已断开全部连接" : (j.S("msg") ?? "失败"), j.B("ok"));
            _selectedId = null;
            DetailBox.Visibility = Visibility.Collapsed;
            await LoadAsync();
        }

        private void Row_Click(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.DataContext is ConnVm vm)) return;
            if (_selectedId == vm.Id)
            {
                _selectedId = null;
                DetailBox.Visibility = Visibility.Collapsed;
            }
            else
            {
                _selectedId = vm.Id;
                ShowDetail(vm);
            }
            _ = LoadAsync();
        }

        private void ShowDetail(ConnVm c)
        {
            DetailBox.Visibility = Visibility.Visible;
            DetailHost.Children.Clear();
            AddKv("目标", c.Host + "  →  " + c.Dest, mono: true);
            AddKv("来源", c.Source, mono: true);
            AddKv("类型", (c.Net + (string.IsNullOrEmpty(c.Type) ? "" : " / " + c.Type)).Trim(), false);
            AddKv("规则", c.RuleText, false);
            AddKv("链路", string.IsNullOrEmpty(c.Chains) ? "—" : c.Chains, false);
            AddKv("流量", $"↑ {c.Up}   ↓ {c.Down}   持续 {c.Duration:0}s", false);
            AddKv("进程", string.IsNullOrEmpty(c.Process) ? "—" : c.Process, mono: true);
        }

        private void AddKv(string k, string v, bool mono)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            g.Children.Add(new TextBlock { Text = k, FontSize = 11.5, Foreground = Res("Fg3") });

            var val = new TextBlock
            {
                Text = v,
                FontSize = mono ? 11.5 : 12,
                FontFamily = mono ? (FontFamily)TryFindResource("MonoFont") : (FontFamily)TryFindResource("UiFont"),
                Foreground = Res("Fg2"),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(val, 1);
            g.Children.Add(val);
            DetailHost.Children.Add(g);
        }

        private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

        private async void CloseOne_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedId)) return;
            var j = await Api.Delete("/api/connections/" + Uri.EscapeDataString(_selectedId));
            _owner.ShowToast(j.B("ok") ? "已断开这一条" : (j.S("msg") ?? "失败"), j.B("ok"));
            _selectedId = null;
            DetailBox.Visibility = Visibility.Collapsed;
            await LoadAsync();
        }

        private void HideDetail_Click(object sender, RoutedEventArgs e)
        {
            _selectedId = null;
            DetailBox.Visibility = Visibility.Collapsed;
            _ = LoadAsync();
        }
    }
}
