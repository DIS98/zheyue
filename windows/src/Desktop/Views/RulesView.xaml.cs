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
    public partial class RulesView : PageBase
    {
        private readonly MainWindow _owner;
        private List<JObj> _rules = new List<JObj>();
        private int _count, _totalHit, _maxHit;
        private bool _loaded, _ruleOk, _rpOk;
        private bool _loading;

        public RulesView(MainWindow owner)
        {
            _owner = owner;
            InitializeComponent();
        }

        private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        private Style St(string key) => TryFindResource(key) as Style;

        public override async void OnActivated()
        {
            if (_loaded) return;
            _loaded = true;
            await ReloadAsync();
        }

        /// <summary>
        /// 首次进页面时内核可能还没起来，那次读取必然失败。
        /// 所以内核一就绪就自动补一次，否则页面会一直停在"控制端口未监听"。
        /// </summary>
        public override async void OnSnap(Snap s)
        {
            if (s == null || !s.CoreRunning || (_ruleOk && _rpOk) || _loading) return;
            await ReloadAsync();
        }

        private async System.Threading.Tasks.Task ReloadAsync()
        {
            _loading = true;
            try
            {
                await LoadRulesAsync();
                await LoadProvidersAsync();
            }
            finally { _loading = false; }
        }

        // ============================================================ 规则

        private string SortTag()
        {
            if (RuleSort.SelectedItem is ComboBoxItem it && it.Tag is string t) return t;
            return "hit";
        }

        private async System.Threading.Tasks.Task LoadRulesAsync()
        {
            var j = await Api.Rules(SortTag());
            if (!j.B("ok"))
            {
                _ruleOk = false;
                RuleTip.Text = j.S("msg");
                return;
            }
            _ruleOk = true;
            _rules = j.List("rules");
            _count = j.I("count");
            _totalHit = j.I("totalHit");
            _maxHit = Math.Max(1, j.I("maxHit"));
            RuleTip.Text = $"{_count} 条 · 累计命中 {_totalHit} 次";
            RenderRules();
        }

        private void RenderRules()
        {
            var kws = (RuleSearch.Text ?? "").Trim().ToLowerInvariant()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            var list = _rules;
            if (kws.Length > 0)
                list = list.Where(r =>
                {
                    var s = (r.S("type") + " " + r.S("payload") + " " + r.S("proxy")).ToLowerInvariant();
                    return kws.All(k => s.Contains(k));
                }).ToList();

            RuleHost.Children.Clear();
            if (list.Count == 0)
            {
                RuleHost.Children.Add(new TextBlock
                {
                    Text = "没有匹配的规则",
                    Style = St("Sub"),
                    Margin = new Thickness(14, 12, 0, 12),
                });
                return;
            }

            int idx = 0;
            foreach (var r in list.Take(400))
            {
                idx++;
                RuleHost.Children.Add(BuildRow(idx, r));
            }
            if (list.Count > 400)
            {
                RuleHost.Children.Add(new TextBlock
                {
                    Text = $"仅显示前 400 条，用搜索缩小范围（共 {list.Count} 条匹配）",
                    Style = St("Sub"),
                    Margin = new Thickness(14, 8, 0, 8),
                });
            }
        }

        private Border BuildRow(int idx, JObj r)
        {
            int hit = r.I("hit");
            var grid = new Grid();

            var cols = new[] { 42.0, 86.0, -1.0, 132.0, 110.0, 62.0 };
            foreach (var w in cols)
                grid.ColumnDefinitions.Add(w < 0
                    ? new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                    : new ColumnDefinition { Width = new GridLength(w) });

            grid.Children.Add(Cell(idx.ToString(), "Sub", 0, HorizontalAlignment.Left));

            var typeBadge = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 1, 6, 1),
                Background = Res("Panel3"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = r.S("type"),
                    FontSize = 10,
                    Foreground = Res("Fg2"),
                },
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(typeBadge, 1);
            grid.Children.Add(typeBadge);

            var payload = new TextBlock
            {
                Text = r.S("payload"),
                FontSize = 11.5,
                FontFamily = TryFindResource("MonoFont") as FontFamily,
                Foreground = Res("Fg"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            Grid.SetColumn(payload, 2);
            grid.Children.Add(payload);

            var proxy = new TextBlock
            {
                Text = Fmt.NormalizeFlags(r.S("proxy")),
                FontSize = 11.5,
                Foreground = Res("Fg2"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetColumn(proxy, 3);
            grid.Children.Add(proxy);

            // 命中分布：细条，宽度按 maxHit 归一
            var barBox = new Grid { VerticalAlignment = VerticalAlignment.Center, Height = 6, Margin = new Thickness(0, 0, 12, 0) };
            barBox.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = Res("Panel3"),
            });
            if (hit > 0)
            {
                barBox.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(3),
                    Background = Res("Accent"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Max(3, 96.0 * hit / _maxHit),
                    Opacity = 0.8,
                });
            }
            Grid.SetColumn(barBox, 4);
            grid.Children.Add(barBox);

            var hitText = new TextBlock
            {
                Text = hit.ToString(),
                FontSize = 11.5,
                Foreground = hit > 0 ? Res("Accent") : Res("Fg3"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = hit > 0 ? FontWeights.SemiBold : FontWeights.Normal,
            };
            Grid.SetColumn(hitText, 5);
            grid.Children.Add(hitText);

            return new Border
            {
                Padding = new Thickness(14, 6, 14, 6),
                BorderBrush = Res("Line"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = grid,
            };
        }

        private TextBlock Cell(string text, string style, int col, HorizontalAlignment align)
        {
            var t = new TextBlock
            {
                Text = text,
                Style = St(style),
                HorizontalAlignment = align,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(t, col);
            return t;
        }

        private void RuleSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (RuleSearchHint != null)
                RuleSearchHint.Visibility = string.IsNullOrEmpty(RuleSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
            if (!IsLoaded) return;
            RenderRules();
        }

        private async void RuleSort_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            await LoadRulesAsync();
            await LoadCustomAsync();
        }


        // ============================================================ 自定义分流

        private void CustomHost_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (CustomHint == null) return;
            CustomHint.Visibility = string.IsNullOrEmpty(CustomHost.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private string PickedTag(ComboBox cb, string fallback)
        {
            var it = cb.SelectedItem as ComboBoxItem;
            return (it != null && it.Tag != null) ? it.Tag.ToString() : fallback;
        }

        private async Task LoadCustomAsync()
        {
            var j = await Api.Get("/api/custom-rules", 8000);
            CustomList.Children.Clear();
            if (!j.B("ok")) return;
            var rules = j.List("rules");
            if (rules.Count == 0)
            {
                CustomList.Children.Add(new TextBlock
                {
                    Text = "还没有自定义规则。上面填个域名、选走代理或直连，点添加即可。",
                    FontSize = 11.5,
                    Margin = new Thickness(0, 8, 0, 2),
                    Foreground = Res("Fg3"),
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }
            foreach (var r in rules)
            {
                var value = r.S("value") ?? "";
                var type = r.S("type") ?? "DOMAIN-SUFFIX";
                var isDirect = (r.S("action") ?? "proxy") == "direct";
                CustomList.Children.Add(CustomRow(value, type, isDirect));
            }
        }

        private UIElement CustomRow(string value, string type, bool isDirect)
        {
            var g = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("Fg"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var typeLabel = type == "DOMAIN" ? "精确" : (type == "DOMAIN-KEYWORD" ? "关键词" : "后缀");
            left.Children.Add(new TextBlock
            {
                Text = typeLabel,
                FontSize = 10.5,
                Margin = new Thickness(8, 0, 0, 0),
                Foreground = Res("Fg3"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            g.Children.Add(left);

            var badge = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Res(isDirect ? "OkSoft" : "AccentSoft"),
                Child = new TextBlock
                {
                    Text = isDirect ? "直连" : "走代理",
                    FontSize = 10.5,
                    Foreground = Res(isDirect ? "Ok" : "Accent"),
                },
            };
            Grid.SetColumn(badge, 1);
            g.Children.Add(badge);

            var del = new Button
            {
                Content = "删除",
                Style = (Style)FindResource("BtnSm"),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            del.Click += async (s2, e2) =>
            {
                del.IsEnabled = false;
                var j = await Api.Delete("/api/custom-rules?value=" + Uri.EscapeDataString(value), 8000);
                if (j.B("ok")) { _owner.ShowToast("已删除 " + value, true); await LoadCustomAsync(); }
                else { del.IsEnabled = true; _owner.ShowToast(j.S("msg") ?? "删除失败", false); }
            };
            Grid.SetColumn(del, 2);
            g.Children.Add(del);
            return g;
        }

        private async void AddCustom_Click(object sender, RoutedEventArgs e)
        {
            var value = (CustomHost.Text ?? "").Trim();
            if (value.Length == 0) { _owner.ShowToast("先填个域名", false); return; }
            BtnAddCustom.IsEnabled = false;
            try
            {
                var j = await Api.Post("/api/custom-rules", Json.Obj(
                    "value", value,
                    "type", PickedTag(CustomType, "DOMAIN-SUFFIX"),
                    "action", PickedTag(CustomAction, "proxy")), 8000);
                if (!j.B("ok")) { _owner.ShowToast(j.S("msg") ?? "添加失败", false); return; }
                CustomHost.Text = "";
                await LoadCustomAsync();
                _owner.ShowToast(j.S("msg") ?? "已添加", true);
            }
            finally { BtnAddCustom.IsEnabled = true; }
        }

        // ============================================================ 预判

        /// <summary>
        /// 占位提示是叠在输入框上的一层 TextBlock，必须跟着输入内容显隐，
        /// 否则它会和用户打的字叠在一起（看起来像"文字重复"）。
        /// </summary>
        private void DiagHost_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (DiagHint == null) return;
            DiagHint.Visibility = string.IsNullOrEmpty(DiagHost.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void Diag_Click(object sender, RoutedEventArgs e)
        {
            var host = (DiagHost.Text ?? "").Trim();
            if (host.Length == 0) { _owner.ShowToast("先填个域名", false); return; }

            BtnDiag.IsEnabled = false;
            BtnDiag.Content = "查询中";
            DiagResult.Content = new TextBlock
            {
                Text = "正在让内核真实连一次 " + host + " …",
                Style = St("Sub"),
            };

            var j = await Api.Post("/api/diagnose", Json.Obj("host", host), 20000);

            BtnDiag.IsEnabled = true;
            BtnDiag.Content = "预判";

            if (!j.B("ok"))
            {
                DiagResult.Content = new Border
                {
                    Background = Res("BadSoft"),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 9, 12, 9),
                    Child = new TextBlock
                    {
                        Text = "没拿到结果：" + (j.S("msg") ?? "未知原因"),
                        FontSize = 12,
                        Foreground = Res("Bad"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
                return;
            }

            var row = new WrapPanel();
            row.Children.Add(Chip("域名", j.S("host"), "Fg"));
            row.Children.Add(Chip("命中规则", j.S("rule"), "Accent"));
            row.Children.Add(Chip("最终节点", Fmt.NormalizeFlags(j.S("proxy")), "Fg"));
            row.Children.Add(Chip("链路", string.Join(" ← ", j.StrList("chains").Select(Fmt.NormalizeFlags)), "Fg2"));
            row.Children.Add(Chip("耗时", j.I("ms") + "ms · " + (j.S("source") == "connections" ? "连接记录" : "内核日志"), "Fg3"));
            DiagResult.Content = row;
        }

        private Border Chip(string label, string value, string colorKey)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 22, 6) };
            box.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 10.5,
                Foreground = Res("Fg3"),
            });
            box.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(value) ? "—" : value,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res(colorKey),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            });
            return new Border { Child = box };
        }

        // ============================================================ 规则集

        private async System.Threading.Tasks.Task LoadProvidersAsync()
        {
            var j = await Api.RuleProviders();
            RpHost.Children.Clear();

            if (!j.B("ok"))
            {
                _rpOk = false;
                RpTip.Text = "内核未运行或接口不可用";
                return;
            }
            _rpOk = true;

            var list = j.List("providers");
            RpTip.Text = $"共 {list.Count} 个";

            if (list.Count == 0)
            {
                RpHost.Children.Add(new TextBlock
                {
                    Text = "当前配置没有 rule-providers",
                    Style = St("Sub"),
                    Margin = new Thickness(0, 6, 0, 6),
                });
                return;
            }

            // 表头
            RpHost.Children.Add(RpHeader());

            foreach (var p in list)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 1) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });

                g.Children.Add(new TextBlock
                {
                    Text = p.S("name"),
                    FontSize = 11.5,
                    Foreground = Res("Fg"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                });

                var beh = new TextBlock
                {
                    Text = p.S("behavior"),
                    FontSize = 10.5,
                    Foreground = Res("Fg2"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(beh, 1);
                g.Children.Add(beh);

                var cnt = new TextBlock
                {
                    Text = p.I("ruleCount").ToString(),
                    FontSize = 11.5,
                    Foreground = Res("Fg2"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(cnt, 2);
                g.Children.Add(cnt);

                var when = new TextBlock
                {
                    Text = (p.S("updatedAt") ?? "").Replace("T", " ").Trim(),
                    FontSize = 10.5,
                    FontFamily = TryFindResource("MonoFont") as FontFamily,
                    Foreground = Res("Fg3"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(when, 3);
                g.Children.Add(when);

                var name = p.S("name");
                var btn = new Button
                {
                    Content = "更新",
                    Style = St("BtnSm"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                btn.Click += async (_, __) =>
                {
                    btn.IsEnabled = false;
                    btn.Content = "更新中";
                    var r = await Api.Post("/api/providers/rules/update", Json.Obj("name", name), 60000);
                    _owner.ShowToast(r.S("msg") ?? (r.B("ok") ? "已更新" : "失败"), r.B("ok"));
                    await LoadProvidersAsync();
                };
                Grid.SetColumn(btn, 4);
                g.Children.Add(btn);

                RpHost.Children.Add(new Border
                {
                    Padding = new Thickness(0, 6, 0, 6),
                    BorderBrush = Res("Line"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = g,
                });
            }
        }

        private Grid RpHeader()
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });

            string[] heads = { "名称", "类型", "规则数", "更新时间", "" };
            for (int i = 0; i < heads.Length; i++)
            {
                var t = new TextBlock { Text = heads[i], Style = St("Sub") };
                Grid.SetColumn(t, i);
                g.Children.Add(t);
            }
            return g;
        }

        private async void RpRefresh_Click(object sender, RoutedEventArgs e) => await LoadProvidersAsync();

        private async void RpUpdateAll_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("更新全部规则集？会重新下载所有 rule-provider。", "折跃",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            var j = await Api.Post("/api/providers/rules/update", Json.Obj("all", true), 180000);
            _owner.ShowToast(j.S("msg") ?? "完成", j.B("ok"));
            await LoadProvidersAsync();
        }
    }
}
