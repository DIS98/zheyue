using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClashDeck.Core;

namespace ClashDeck.Views
{
    public partial class ProxiesView : PageBase
    {
        private readonly MainWindow _owner;
        private readonly ObservableCollection<GroupVm> _groups = new ObservableCollection<GroupVm>();
        private readonly Dictionary<string, GroupVm> _byName = new Dictionary<string, GroupVm>();
        private Dictionary<string, DelayStat> _stats = new Dictionary<string, DelayStat>(StringComparer.Ordinal);
        private readonly System.Windows.Threading.DispatcherTimer _statTimer;

        public ProxiesView(MainWindow owner)
        {
            _owner = owner;
            InitializeComponent();
            GroupList.ItemsSource = _groups;

            _statTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            // 页面是懒创建并缓存的，切走后控件还在，定时器也还在跑。
            // 没有 IsVisible 守卫时，停在「概览」页也会每 12 秒拉一次延迟历史 ——
            // 日志页、连接页、概览页都有这道守卫，只有这里漏了。
            _statTimer.Tick += async (_, __) => { if (IsVisible) await LoadStatsAsync(); };
            _statTimer.Start();
        }

        public override async void OnActivated() => await LoadStatsAsync();

        private bool _statBusy;

        /// <summary>丢包率来自面板记的延迟历史，12 秒拉一次就够。</summary>
        private async Task LoadStatsAsync()
        {
            if (_statBusy) return;   // 后端卡住时不叠加请求
            _statBusy = true;
            try
            {
                await LoadStatsCoreAsync();
            }
            finally { _statBusy = false; }
        }

        private async Task LoadStatsCoreAsync()
        {
            var j = await Api.DelayHistory(null);
            if (!j.B("ok")) return;
            var map = new Dictionary<string, DelayStat>(StringComparer.Ordinal);
            foreach (var kv in j["all"].Pairs)
            {
                var v = kv.Value;
                var st = new DelayStat
                {
                    LossRate = v.D("lossRate"),
                    Avg = v.D("avg"),
                    Min = v.D("min"),
                    Max = v.D("max"),
                    Ok = v.D("ok"),
                    Fail = v.D("fail"),
                };
                foreach (var s in v.List("samples"))
                    if (s.Raw is double d) st.Samples.Add(d);
                map[kv.Key] = st;
            }
            _stats = map;

            foreach (var g in _groups)
                foreach (var n in g.All)
                    n.Loss = _stats.TryGetValue(n.Name, out var st) ? st.LossRate : 0;
        }

        // ============================================================ 数据同步

        public override void OnSnap(Snap s)
        {
            if (s == null) return;

            // 组：按名字对齐，新增/删除
            var wanted = s.Groups;
            for (int i = _groups.Count - 1; i >= 0; i--)
            {
                if (wanted.All(w => w.Name != _groups[i].Name))
                {
                    _byName.Remove(_groups[i].Name);
                    _groups.RemoveAt(i);
                }
            }

            foreach (var g in wanted)
            {
                if (!_byName.TryGetValue(g.Name, out var vm))
                {
                    vm = new GroupVm(g.Name);
                    _byName[g.Name] = vm;
                    _groups.Add(vm);
                }
                vm.Type = g.Type;
                vm.Now = g.Now;

                // 结构没变就只刷数值，避免整表重建
                var sig = string.Join("\u0001", g.All);
                if (vm.AllSig != sig)
                {
                    vm.AllSig = sig;
                    vm.All.Clear();
                    foreach (var name in g.All) vm.All.Add(new NodeVm(g.Name, name));
                    vm.NotifyCount();
                }

                var pool = new Dictionary<string, NodeVm>(vm.All.Count);
                foreach (var n in vm.All) pool[n.Name] = n;

                foreach (var name in g.All)
                {
                    if (!pool.TryGetValue(name, out var nv)) continue;
                    nv.Delay = g.DelayOf(name);
                    nv.IsSelected = name == g.Now;

                    var st = _stats != null && _stats.TryGetValue(name, out var ds) ? ds : null;
                    nv.Loss = st != null ? st.LossRate : 0;
                }
            }

            Reshuffle();
            HealEmptyNames();
        }

        /// <summary>
        /// 自愈：如果某个节点的显示名变成空白（字体缺失、异常、绑定失败都可能导致），
        /// 就把结构签名清掉，让下一次刷新整体重建 —— 不用等用户去点"更新订阅"。
        /// </summary>
        private void HealEmptyNames()
        {
            foreach (var g in _groups)
            {
                foreach (var n in g.All)
                {
                    if (!string.IsNullOrWhiteSpace(n.DisplayName)) continue;
                    Paths.WriteLog("heal: empty display name detected, rebuilding group " + g.Name);
                    foreach (var gg in _groups)
                    {
                        gg.AllSig = "";
                        gg.All.Clear();
                        gg.Nodes.Clear();
                    }
                    return;
                }
            }
        }

        private string SortTag()
        {
            if (Sort.SelectedItem is ComboBoxItem it && it.Tag is string t) return t;
            return "delay";
        }

        private string[] Keywords()
        {
            var raw = (Search.Text ?? "").Trim().ToLowerInvariant();
            if (raw.Length == 0) return new string[0];
            return raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private void Reshuffle()
        {
            var kws = Keywords();
            var mode = SortTag();
            int shown = 0, total = 0;

            foreach (var g in _groups)
            {
                IEnumerable<NodeVm> q = g.All;
                if (kws.Length > 0)
                    q = q.Where(n => kws.All(k => (n.DisplayName + " " + n.Name).ToLowerInvariant().Contains(k)));

                switch (mode)
                {
                    case "delay":
                        q = q.OrderBy(n => n.Delay <= 0 ? double.MaxValue : n.Delay).ThenBy(n => n.DisplayName, StringComparer.Ordinal);
                        break;
                    case "delay-desc":
                        q = q.OrderByDescending(n => n.Delay).ThenBy(n => n.DisplayName, StringComparer.Ordinal);
                        break;
                    case "name":
                        q = q.OrderBy(n => n.DisplayName, StringComparer.CurrentCulture);
                        break;
                }

                var list = q.ToList();
                shown += list.Count;
                total += g.All.Count;

                if (!list.SequenceEqual(g.Nodes))
                {
                    g.Nodes.Clear();
                    foreach (var n in list) g.Nodes.Add(n);
                }
            }

            Tip.Text = kws.Length > 0 ? $"筛出 {shown} / 共 {total} 个节点" : $"共 {total} 个节点";
        }

        /// <summary>把界面里实际持有的节点数和显示名导进日志，用于对照快照数据。</summary>
        public void DumpState()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"groups={_groups.Count} | ");
            int empty = 0;
            foreach (var g in _groups)
            {
                string first = g.Nodes.Count > 0 ? g.Nodes[0].DisplayName : "-";
                sb.Append($"[{g.Name} all={g.All.Count} ui={g.Nodes.Count} first={first}] ");
                foreach (var n in g.All) if (string.IsNullOrWhiteSpace(n.DisplayName)) empty++;
            }
            Paths.WriteLog("UISTATE proxies: " + sb);
            Paths.WriteLog($"UISTATE empty-display={empty}");
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchHint != null)
                SearchHint.Visibility = string.IsNullOrEmpty(Search.Text) ? Visibility.Visible : Visibility.Collapsed;
            Reshuffle();
        }

        private void Sort_SelectionChanged(object sender, SelectionChangedEventArgs e) => Reshuffle();

        // ============================================================ 交互

        private async void Node_Click(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.DataContext is NodeVm vm)) return;

            var j = await Api.Post("/api/proxies/select", Json.Obj("group", vm.Group, "name", vm.Name));
            _owner.ShowToast(j.B("ok") ? "已切换到 " + vm.Name : (j.S("msg") ?? "切换失败"), j.B("ok"));
        }

        private async void Delay_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!(sender is FrameworkElement fe) || !(fe.DataContext is NodeVm vm)) return;

            var badge = sender as Border;
            if (badge == null || !badge.IsEnabled) return;
            badge.IsEnabled = false;

            var j = await Api.Get("/api/proxies/delay?name=" + Api.Q(vm.Name) + "&timeout=5000", 20000);
            var delay = j.D("delay");
            // 失败写成 -1，而不是沿用 0。0 的含义是"从没测过"，两者显示不同：
            // 0 → "—"，-1 → "超时"。混用会让刚打开的界面看起来全挂。
            vm.Delay = delay > 0 ? delay : -1;
            badge.IsEnabled = true;

            _owner.ShowToast(delay > 0 ? $"{vm.Name}：{Fmt.Delay(delay)}" : $"{vm.Name}：测速失败", delay > 0);
        }

        private async void GroupTest_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn) || !(btn.Tag is GroupVm vm)) return;
            btn.IsEnabled = false;
            var old = btn.Content;
            btn.Content = "测速中";

            var j = await Api.Get("/api/proxies/delay?group=" + Api.Q(vm.Name) + "&timeout=5000", 120000);

            btn.Content = old;
            btn.IsEnabled = true;
            _owner.ShowToast(j.B("ok") ? vm.Name + " 测速完成" : (j.S("msg") ?? "测速失败"), j.B("ok"));
        }

        private async void TestAll_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn)) return;
            btn.IsEnabled = false;
            var old = btn.Content;
            int done = 0;

            var names = _groups.Select(g => g.Name).ToList();
            foreach (var name in names)
            {
                btn.Content = $"测速中 {++done}/{names.Count}";
                await Api.Get("/api/proxies/delay?group=" + Api.Q(name) + "&timeout=5000", 120000);
            }

            btn.Content = old;
            btn.IsEnabled = true;
            _owner.ShowToast($"已测完 {names.Count} 个策略组");
        }
    }
}
