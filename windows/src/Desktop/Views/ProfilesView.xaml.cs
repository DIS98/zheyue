using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClashDeck.Core;

namespace ClashDeck.Views
{
    public partial class ProfilesView : PageBase
    {
        private readonly MainWindow _owner;
        private Snap _last;
        private string _profileSig = "";

        public ProfilesView(MainWindow owner)
        {
            _owner = owner;
            InitializeComponent();
        }

        private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        private Style St(string key) => TryFindResource(key) as Style;

        public override void OnSnap(Snap s)
        {
            if (s == null) return;
            _last = s;

            var sig = string.Join("|", s.Profiles.Select(p =>
                p.File + "#" + p.Size + "#" + (p.Quota?.UsedPct ?? -1) + "#" + (p.File == s.ActiveProfile)));
            if (sig != _profileSig)
            {
                _profileSig = sig;
                RenderProfiles(s);
                FillMergeOptions(s);
            }

            FillSettings(s);
        }

        // ============================================================ 配置列表

        private void RenderProfiles(Snap s)
        {
            ProfileHost.Children.Clear();
            Tip.Text = $"{s.Profiles.Count} 份";

            if (s.Profiles.Count == 0)
            {
                ProfileHost.Children.Add(new TextBlock
                {
                    Text = "还没有配置。用下面的「导入订阅」拉一份，或把 YAML 文件拖进窗口。",
                    Style = St("Sub"),
                    Margin = new Thickness(16, 16, 16, 16),
                });
                return;
            }

            var sorted = s.Profiles
                .OrderByDescending(p => p.File == s.ActiveProfile)
                .ThenByDescending(p => p.Mtime)
                .ToList();

            foreach (var p in sorted)
            {
                ProfileHost.Children.Add(BuildProfileRow(s, p));
            }
        }

        private Border BuildProfileRow(Snap s, ProfileInfo p)
        {
            bool active = p.File == s.ActiveProfile;

            var left = new StackPanel();

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock
            {
                Text = p.Name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("Fg"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 420,
            });
            if (active)
            {
                titleRow.Children.Add(new Border
                {
                    Background = Res("Accent"),
                    CornerRadius = new CornerRadius(999),
                    Padding = new Thickness(7, 1, 7, 1),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = "使用中", FontSize = 10, Foreground = Brushes.White },
                });
            }
            left.Children.Add(titleRow);

            var meta = new List<string>();
            if (p.IsSub && !string.IsNullOrEmpty(p.SubUrl))
            {
                try { meta.Add(new Uri(p.SubUrl).Host); } catch { meta.Add("订阅"); }
            }
            else meta.Add("本地文件");
            meta.Add(Fmt.Bytes(p.Size));
            meta.Add(p.LastUpdate > 0 ? "更新于 " + Fmt.Ago(p.LastUpdate) : "导入于 " + Fmt.Ago(p.Mtime));

            left.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", meta),
                FontSize = 11,
                Foreground = Res("Fg3"),
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            if (p.Quota != null)
            {
                var bar = new Grid { Height = 5, Margin = new Thickness(0, 8, 0, 0), MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left };
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Res("Panel3") });
                bar.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(3),
                    Background = Res(p.Quota.UsedPct > 90 ? "Bad" : "Accent"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Max(2, 4.6 * p.Quota.UsedPct),
                    Opacity = 0.85,
                });
                left.Children.Add(bar);

                var qline = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
                qline.Children.Add(new TextBlock
                {
                    Text = $"已用 {Fmt.Bytes(p.Quota.Used)} / {Fmt.Bytes(p.Quota.Total)}",
                    FontSize = 10.5,
                    Foreground = Res("Fg3"),
                });
                qline.Children.Add(new TextBlock
                {
                    Text = $"   剩余 {Fmt.Bytes(p.Quota.Remain)}",
                    FontSize = 10.5,
                    Foreground = Res("Fg2"),
                    FontWeight = FontWeights.SemiBold,
                });
                if (p.Quota.Expire > 0)
                {
                    qline.Children.Add(new TextBlock
                    {
                        Text = "   " + Fmt.Expire(p.Quota.Expire),
                        FontSize = 10.5,
                        Foreground = Res("Fg3"),
                    });
                }
                left.Children.Add(qline);
            }

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            if (!active)
            {
                var use = new Button { Content = "激活", Style = St("BtnSm") };
                use.Click += async (_, __) =>
                {
                    use.IsEnabled = false;
                    var j = await Api.Post("/api/profiles/activate", Json.Obj("file", p.File), 30000);
                    _owner.ShowToast(j.S("msg") ?? (j.B("ok") ? "已激活" : "失败"), j.B("ok"));
                    IsEnabled = true;
                };
                actions.Children.Add(use);
            }

            if (p.IsSub)
            {
                var upd = new Button { Content = "更新", Style = St("BtnSm"), Margin = new Thickness(6, 0, 0, 0) };
                upd.Click += async (_, __) =>
                {
                    upd.IsEnabled = false;
                    upd.Content = "更新中";
                    var j = await Api.Post("/api/subscriptions/update", Json.Obj("file", p.File), 60000);
                    _owner.ShowToast(j.S("msg") ?? "完成", j.B("ok"));
                    upd.Content = "更新";
                    upd.IsEnabled = true;
                };
                actions.Children.Add(upd);
            }

            var del = new Button { Content = "删除", Style = St("BtnSm"), Margin = new Thickness(6, 0, 0, 0) };
            del.SetValue(ForegroundProperty, Res("Bad"));
            del.Click += async (_, __) =>
            {
                if (MessageBox.Show($"删除「{p.Name}」？", "折跃", MessageBoxButton.OKCancel,
                        MessageBoxImage.Question) != MessageBoxResult.OK) return;
                var j = await Api.Delete("/api/profiles?file=" + Api.Q(p.File));
                _owner.ShowToast(j.S("msg") ?? (j.B("ok") ? "已删除" : "失败"), j.B("ok"));
            };
            actions.Children.Add(del);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(left);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);

            return new Border
            {
                Padding = new Thickness(16, 12, 16, 12),
                BorderBrush = Res("Line"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Background = active ? Res("Panel2") : Brushes.Transparent,
                Child = grid,
            };
        }

        // ============================================================ 设置回填

        private void FillSettings(Snap s)
        {
            if (_last == null) return;

            if (!IsEditing(TestUrl)) TestUrl.Text = s.Env.TestUrl ?? "";
            if (!IsEditing(TestTimeout)) TestTimeout.Text = s.Env.DelayTimeout.ToString();
            TestUrlTip.Text = "当前：" + (string.IsNullOrEmpty(s.Env.TestUrl) ? "默认" : s.Env.TestUrl);

            SelectTag(AutoDelay, s.Env.AutoDelayMinutes);

            if (!IsEditing(CorePath)) CorePath.Text = s.CorePath;
            if (!IsEditing(MixedPort)) MixedPort.Text = s.MixedPort.ToString();
            if (!IsEditing(CtrlPort)) CtrlPort.Text = s.ControllerPort.ToString();
            if (!IsEditing(WebPort)) WebPort.Text = s.WebPort.ToString();

            SwAutoCore.IsChecked = s.Env.AutoStartCore;
            SwAutoProxy.IsChecked = s.Env.AutoStartProxy;
            SwAutostart.IsChecked = s.Env.Autostart;
        }

        private bool IsEditing(TextBox tb) => tb != null && tb.IsKeyboardFocusWithin;

        private static void SelectTag(ComboBox cb, int value)
        {
            foreach (var item in cb.Items)
                if (item is ComboBoxItem it && it.Tag is string t && t == value.ToString())
                {
                    cb.SelectedItem = it;
                    return;
                }
            if (cb.Items.Count > 0 && cb.SelectedItem == null) cb.SelectedIndex = 0;
        }

        private static int TagOf(ComboBox cb)
        {
            if (cb.SelectedItem is ComboBoxItem it && it.Tag is string t && int.TryParse(t, out var v)) return v;
            return 0;
        }

        // ============================================================ 订阅导入

        private async void SubImport_Click(object sender, RoutedEventArgs e)
        {
            var url = (SubUrl.Text ?? "").Trim();
            if (url.Length == 0) { _owner.ShowToast("先填订阅地址", false); return; }

            var j = await Api.Post("/api/profiles/fetch", Json.Obj(
                "name", (SubName.Text ?? "").Trim(),
                "url", url,
                "intervalHours", TagOf(SubInterval)), 60000);

            _owner.ShowToast(j.S("msg") ?? (j.B("ok") ? "已导入" : "失败"), j.B("ok"));
            if (j.B("ok"))
            {
                SubName.Text = "";
                SubUrl.Text = "";
                await _owner.RefreshNow();
            }
        }

        // ============================================================ 测速设置

        private async void TestSave_Click(object sender, RoutedEventArgs e)
        {
            var url = (TestUrl.Text ?? "").Trim();
            if (!int.TryParse((TestTimeout.Text ?? "").Trim(), out var timeout)) timeout = 5000;

            var j = await Api.Post("/api/settings", Json.Obj("testUrl", url, "delayTimeout", timeout));
            _owner.ShowToast(j.S("msg") ?? (j.B("ok") ? "已保存" : "保存失败"), j.B("ok"));
            if (j.B("ok")) await _owner.RefreshNow();
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string url) TestUrl.Text = url;
        }

        // ============================================================ 端口与联动

        private async void PortSave_Click(object sender, RoutedEventArgs e)
        {
            // ---------------------------------------------------------- 就地校验（不发请求）
            // 以前 TryParse 失败就把该字段**静默丢掉**，后端照样 ok:true，
            // 界面报"已保存"，然后 FillSettings 又把旧值填回来 —— 一次"假装成功"。
            ResetPortMark(MixedPort); ResetPortMark(CtrlPort); ResetPortMark(WebPort);

            if (!TryReadPort(MixedPort, "混合端口", out var mp)) return;
            if (!TryReadPort(CtrlPort, "控制端口", out var cp)) return;
            if (!TryReadPort(WebPort, "面板端口", out var wp)) return;

            if (mp == cp || mp == wp || cp == wp)
            {
                var name = mp == cp ? "混合端口与控制端口" : (mp == wp ? "混合端口与面板端口" : "控制端口与面板端口");
                _owner.ShowToast(name + " 不能相同（都是 " + (mp == cp ? mp : (mp == wp ? mp : cp)) + "）", false);
                return;
            }

            // ---------------------------------------------------------- 面板端口单独处理
            // 后端只写盘、不热切监听，所以"保存成功"和"已生效"是两件事。
            var curWeb = _owner.CurrentWebPort;
            if (curWeb > 0 && wp != curWeb)
            {
                var ask = MessageBox.Show(
                    "面板端口从 " + curWeb + " 改成 " + wp + " 需要重启本应用才生效。\n"
                    + "中间的这段时间，界面仍然连在旧端口上。\n\n"
                    + "现在重启吗？（点「否」= 保持原来的 " + curWeb + "）",
                    "面板端口改动需要重启", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                    == MessageBoxResult.Yes;
                if (!ask)
                {
                    WebPort.Text = curWeb.ToString();   // 回滚输入框，别让界面显示一个没生效的值
                    return;
                }
                var err = await _owner.SetWebPortAsync(wp);
                if (!string.IsNullOrEmpty(err))
                {
                    _owner.ShowToast(err, false);
                    WebPort.Text = curWeb.ToString();
                    return;
                }
                await _owner.RestartBackendAsync();
                return;
            }

            // ---------------------------------------------------------- 其余设置照常保存
            var body = Json.Obj();
            var core = (CorePath.Text ?? "").Trim();
            if (core.Length > 0) body["corePath"] = core;
            body["mixedPort"] = mp;
            body["controllerPort"] = cp;

            var j = await Api.Post("/api/settings", body);
            _owner.ShowToast(j.S("msg") ?? (j.B("ok") ? "已保存" : "保存失败"), j.B("ok"));
            if (j.B("ok")) await _owner.RefreshNow();
        }

        /// <summary>读一个端口框：非空、纯数字、1–65535。不合法就地标红并不发请求。</summary>
        private bool TryReadPort(TextBox box, string label, out int port)
        {
            port = 0;
            var text = (box.Text ?? "").Trim();
            if (text.Length == 0)
            {
                MarkBad(box, label + "不能为空");
                return false;
            }
            foreach (var ch in text)
            {
                if (ch < '0' || ch > '9')
                {
                    MarkBad(box, label + "只能是数字");
                    return false;
                }
            }
            if (!int.TryParse(text, out port) || port < 1 || port > 65535)
            {
                MarkBad(box, label + "必须是 1–65535 的整数");
                return false;
            }
            return true;
        }

        private void MarkBad(TextBox box, string msg)
        {
            box.BorderBrush = (Brush)FindResource("Bad");
            box.ToolTip = msg;
            box.Focus();
            box.SelectAll();
            _owner.ShowToast(msg, false);
        }

        private void ResetPortMark(TextBox box)
        {
            box.ClearValue(Border.BorderBrushProperty);
            box.ToolTip = null;
        }

        private async void SwAutoCore_Click(object sender, RoutedEventArgs e)
        {
            var j = await Api.Post("/api/settings", Json.Obj("autoStartCore", SwAutoCore.IsChecked == true));
            _owner.ShowToast(j.S("msg") ?? "已保存", j.B("ok"));
            if (!j.B("ok")) SwAutoCore.IsChecked = !SwAutoCore.IsChecked;
        }

        private async void SwAutoProxy_Click(object sender, RoutedEventArgs e)
        {
            var j = await Api.Post("/api/settings", Json.Obj("autoStartProxy", SwAutoProxy.IsChecked == true));
            _owner.ShowToast(j.S("msg") ?? "已保存", j.B("ok"));
            if (!j.B("ok")) SwAutoProxy.IsChecked = !SwAutoProxy.IsChecked;
        }

        private async void SwAutostart_Click(object sender, RoutedEventArgs e)
        {
            var j = await Api.Post("/api/autostart", Json.Obj("enable", SwAutostart.IsChecked == true));
            _owner.ShowToast(j.S("msg") ?? "已保存", j.B("ok"));
            if (!j.B("ok")) SwAutostart.IsChecked = !SwAutostart.IsChecked;
        }

        // ============================================================ 粘贴

        private async void PasteSave_Click(object sender, RoutedEventArgs e)
        {
            var text = PasteBox.Text ?? "";
            if (text.IndexOf("proxies", StringComparison.OrdinalIgnoreCase) < 0 &&
                text.IndexOf("proxy-providers", StringComparison.OrdinalIgnoreCase) < 0)
            {
                _owner.ShowToast("内容里没有 proxies，可能不是 Clash 配置", false);
                return;
            }

            var j = await Api.Post("/api/profiles", Json.Obj(
                "name", (PasteName.Text ?? "").Trim(),
                "content", text), 30000);

            _owner.ShowToast(j.S("msg") ?? (j.B("ok") ? "已保存" : "失败"), j.B("ok"));
            if (j.B("ok")) { PasteBox.Text = ""; PasteName.Text = ""; await _owner.RefreshNow(); }
        }

        // ============================================================ 备份 / 恢复

        private async void Backup_Click(object sender, RoutedEventArgs e)
        {
            var j = await Api.Backup();
            if (!j.B("ok")) { _owner.ShowToast("导出失败", false); return; }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"clash-deck-backup-{DateTime.Now:yyyyMMdd-HHmm}.json",
                Filter = "备份文件 (*.json)|*.json",
                Title = "导出备份",
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                File.WriteAllText(dlg.FileName, Json.Stringify(j.Get("bundle")));
                _owner.ShowToast($"已导出 {j["bundle"].List("profiles").Count} 份配置");
            }
            catch (Exception ex) { _owner.ShowToast("写入失败：" + ex.Message, false); }
        }

        private async void RestorePick_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "备份文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                Title = "导入备份",
            };
            if (dlg.ShowDialog() != true) return;

            JObj bundle;
            try
            {
                var root = new JObj(Json.Parse(File.ReadAllText(dlg.FileName)));
                bundle = root.Has("bundle") ? root["bundle"] : root;
            }
            catch { _owner.ShowToast("不是合法的备份文件", false); return; }

            if (!bundle.Has("profiles")) { _owner.ShowToast("备份里没有配置", false); return; }

            var names = string.Join("、", bundle.List("profiles").Select(p => p.S("file")));
            var answer = MessageBox.Show(
                $"备份里有 {bundle.List("profiles").Count} 份配置：\n{names}\n\n点「是」= 覆盖同名配置；点「否」= 只补缺失的",
                "导入备份", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;

            var j = await Api.Post("/api/restore", Json.Obj("bundle", bundle.Raw, "overwrite", answer == MessageBoxResult.Yes), 60000);
            _owner.ShowToast(j.S("msg") ?? (j.B("ok") ? "已导入" : "失败"), j.B("ok"));
            await _owner.RefreshNow();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await _owner.RefreshNow();

        // ============================================================ 合并

        private void FillMergeOptions(Snap s)
        {
            var prevBase = (MergeBase.SelectedItem as ComboBoxItem)?.Tag as string;
            MergeBase.Items.Clear();
            foreach (var p in s.Profiles)
                MergeBase.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.File });

            if (!string.IsNullOrEmpty(prevBase))
                foreach (ComboBoxItem it in MergeBase.Items)
                    if ((string)it.Tag == prevBase) { MergeBase.SelectedItem = it; break; }

            if (MergeBase.SelectedItem == null)
            {
                var active = MergeBase.Items.Cast<ComboBoxItem>()
                    .FirstOrDefault(i => (string)i.Tag == s.ActiveProfile);
                MergeBase.SelectedItem = active ?? (MergeBase.Items.Count > 0 ? MergeBase.Items[0] : null);
            }

            MergeExtras.Children.Clear();
            var subs = s.Profiles.Where(p => p.IsSub).ToList();
            MergeTip.Text = subs.Count > 0 ? $"{subs.Count} 份订阅可用" : "没有订阅导入的配置";

            foreach (var p in subs)
            {
                var cb = new CheckBox
                {
                    Content = p.Name,
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 16, 4),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Foreground = Res("Fg2"),
                    Tag = p.File,
                };
                MergeExtras.Children.Add(cb);
            }
        }

        private async void Merge_Click(object sender, RoutedEventArgs e)
        {
            var baseFile = (MergeBase.SelectedItem as ComboBoxItem)?.Tag as string;
            var extras = MergeExtras.Children.OfType<CheckBox>()
                .Where(c => c.IsChecked == true)
                .Select(c => c.Tag as string)
                .Where(x => !string.IsNullOrEmpty(x))
                .ToList();

            if (string.IsNullOrEmpty(baseFile)) { _owner.ShowToast("先选基础配置", false); return; }
            if (extras.Count == 0) { _owner.ShowToast("至少勾一份订阅", false); return; }

            MergeResult.Content = new TextBlock
            {
                Text = "正在生成并用内核校验（约 1-3 秒）…",
                Style = St("Sub"),
            };

            var j = await Api.Post("/api/merge", Json.Obj(
                "base", baseFile,
                "extras", extras,
                "name", (MergeName.Text ?? "").Trim()), 60000);

            if (j.B("ok"))
            {
                MergeResult.Content = new Border
                {
                    Background = Res("OkSoft"),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 9, 12, 9),
                    Child = new TextBlock
                    {
                        Text = $"已生成 {j.S("file")}，内核校验通过（provider：{string.Join("、", j.StrList("providers"))}）",
                        FontSize = 12,
                        Foreground = Res("Ok"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
                await _owner.RefreshNow();
            }
            else
            {
                var box = new StackPanel();
                box.Children.Add(new TextBlock
                {
                    Text = "没有生成：" + j.S("msg"),
                    FontSize = 12,
                    Foreground = Res("Bad"),
                    TextWrapping = TextWrapping.Wrap,
                });
                var detail = j.S("detail");
                if (!string.IsNullOrEmpty(detail))
                {
                    box.Children.Add(new TextBlock
                    {
                        Text = detail,
                        FontFamily = TryFindResource("MonoFont") as FontFamily,
                        FontSize = 10.5,
                        Foreground = Res("Fg3"),
                        Margin = new Thickness(0, 6, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
                MergeResult.Content = new Border
                {
                    Background = Res("BadSoft"),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 9, 12, 9),
                    Child = box,
                };
            }
        }
    }
}
