using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using ClashDeck.Core;

namespace ClashDeck.Views
{
    /// <summary>
    /// 节点视图模型。只有真正变化的属性才发通知 —— 100 个节点每 2 秒全量重建控件会明显卡顿，
    /// 而这里延迟变了只重绘那一行文字。
    /// </summary>
    public sealed class NodeVm : INotifyPropertyChanged
    {
        public string Name { get; }
        public string Group { get; }

        /// <summary>
        /// 界面显示用：国旗 emoji 换成国家代码，避免渲染成 "jpJapan 02"。
        /// 构造时算好并缓存：这是被绑定反复读取的属性，做成计算属性的话
        /// 一旦某次抛异常，WPF 会记住失败状态，而它又不发属性通知 ——
        /// 结果是整段节点名永久空白，直到列表重建才恢复。
        /// </summary>
        public string DisplayName { get; }

        private static string SafeDisplay(string raw)
        {
            try
            {
                var r = Fmt.NormalizeFlags(raw);
                return string.IsNullOrEmpty(r) ? (raw ?? "") : r;
            }
            catch { return raw ?? ""; }
        }

        private double _delay;
        private bool _sel;
        private double _loss;

        public NodeVm(string group, string name)
        {
            Group = group;
            Name = name;
            DisplayName = SafeDisplay(name);
        }

        public double Delay
        {
            get => _delay;
            set
            {
                if (Math.Abs(_delay - value) < 0.5) return;
                _delay = value;
                Raise(nameof(Delay));
                Raise(nameof(DelayText));
                Raise(nameof(Tier));
            }
        }

        public double Loss
        {
            get => _loss;
            set
            {
                if (Math.Abs(_loss - value) < 0.5) return;
                _loss = value;
                Raise(nameof(Loss));
                Raise(nameof(LossText));
                Raise(nameof(HasLoss));
            }
        }

        public string DelayText => Fmt.Delay(_delay);
        public string LossText => _loss > 0 ? $"{_loss:0}%" : "";
        public bool HasLoss => _loss > 0;
        public DelayTier Tier => Core.Delay.Of(_delay);

        public bool IsSelected
        {
            get => _sel;
            set
            {
                if (_sel == value) return;
                _sel = value;
                Raise(nameof(IsSelected));
            }
        }

        public string Tooltip
        {
            get
            {
                string s = DisplayName + "\n延迟 " + DelayText;
                if (_loss > 0) s += " · 丢包 " + LossText;
                return s + "\n点整行切换；点延迟数字只测这一条";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public sealed class GroupVm : INotifyPropertyChanged
    {
        public string Name { get; }
        public ObservableCollection<NodeVm> Nodes { get; } = new ObservableCollection<NodeVm>();

        /// <summary>全量节点（不经筛选），筛选只看这个。</summary>
        public List<NodeVm> All { get; } = new List<NodeVm>();
        public string AllSig = "";

        private string _type = "";
        private string _now = "";

        public GroupVm(string name) { Name = name; }

        public string Type
        {
            get => _type;
            set { if (_type != value) { _type = value; Raise(nameof(Type)); Raise(nameof(TypeText)); } }
        }

        public string TypeText
        {
            get
            {
                switch (_type)
                {
                    case "Selector": return "手动";
                    case "URLTest": return "自动";
                    case "Fallback": return "故障转移";
                    case "LoadBalance": return "负载均衡";
                    case "Relay": return "链式";
                    default: return _type;
                }
            }
        }

        public string Now
        {
            get => _now;
            set
            {
                if (_now == value) return;
                _now = value;
                Raise(nameof(Now));
                Raise(nameof(DisplayNow));
            }
        }

        /// <summary>组头显示的当前节点：同样是"国旗转国家代码"，保持全局一致。</summary>
        public string DisplayNow
        {
            get
            {
                try { return string.IsNullOrEmpty(_now) ? "—" : Fmt.NormalizeFlags(_now); }
                catch { return _now ?? "—"; }
            }
        }

        public string CountText => $"{All.Count} 个节点";

        public void NotifyCount() => Raise(nameof(CountText));

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
