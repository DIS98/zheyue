using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ClashDeck.Controls
{
    /// <summary>
    /// 双线流量图（自绘）。不用第三方图表库：就两条折线 + 一块渐变填充，
    /// 手画比引依赖更可控，也不会因为库的样式而在深色主题下发白。
    /// </summary>
    public sealed class TrafficChart : FrameworkElement
    {
        private List<double[]> _pts = new List<double[]>();
        private double _peak;

        public void SetData(List<double[]> pts)
        {
            _pts = pts ?? new List<double[]>();
            InvalidateVisual();
        }

        public double Peak => _peak;

        private static Color C(string key)
        {
            var b = Application.Current.TryFindResource(key) as SolidColorBrush;
            return b?.Color ?? Colors.Gray;
        }

        private static Brush B(string key)
        {
            return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 24 || h < 24) return;

            // 网格：3 条水平线，用极淡的分隔色
            var gridPen = new Pen(B("Line"), 1);
            gridPen.Freeze();
            for (int i = 0; i <= 3; i++)
            {
                double y = Math.Round(h * i / 3.0) + 0.5;
                dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            }

            if (_pts.Count < 2)
            {
                DrawText(dc, "启动内核后开始采样", new Point(6, h / 2 - 8), B("Fg3"), 11);
                return;
            }

            // 峰值取数据最大值，向上留 15% 余量；最少按 1 KB/s 画，避免小流量时线条抖成锯齿
            double peak = 1024;
            foreach (var p in _pts)
            {
                if (p[0] > peak) peak = p[0];
                if (p[1] > peak) peak = p[1];
            }
            peak *= 1.15;
            _peak = peak;

            int n = _pts.Count;
            // 点太多就抽稀，屏幕上 1px 一个点没有意义
            int step = Math.Max(1, n / Math.Max(1, (int)(w / 2)));
            var idx = new List<int>();
            for (int i = 0; i < n; i += step) idx.Add(i);
            if (idx[idx.Count - 1] != n - 1) idx.Add(n - 1);

            Func<int, double> X = i => w * i / Math.Max(1, n - 1);
            Func<double, double> Y = v => h - 1 - (h - 8) * Math.Min(1.0, Math.Max(0, v / peak));

            // 下行渐变填充
            var downColor = C("Accent");
            var grad = new LinearGradientBrush(
                Color.FromArgb(64, downColor.R, downColor.G, downColor.B),
                Color.FromArgb(0, downColor.R, downColor.G, downColor.B),
                new Point(0, 0), new Point(0, 1));
            grad.Freeze();

            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(X(idx[0]), h - 1), true, true);
                foreach (var i in idx) ctx.LineTo(new Point(X(i), Y(_pts[i][1])), true, false);
                ctx.LineTo(new Point(X(idx[idx.Count - 1]), h - 1), true, false);
            }
            area.Freeze();
            dc.DrawGeometry(grad, null, area);

            // 上行线（暖色）
            DrawLine(dc, idx, i => Y(_pts[i][0]), X, B("Warn"), 1.3, 0.85);
            // 下行线（主色，画在上层）
            DrawLine(dc, idx, i => Y(_pts[i][1]), X, B("Accent"), 1.8, 1.0);

            // 峰值标注
            var label = Core.Fmt.Speed(peak / 1.15);
            var ft = Text(label, B("Fg3"), 10);
            dc.DrawText(ft, new Point(w - ft.Width - 4, 2));
        }

        private void DrawLine(DrawingContext dc, List<int> idx, Func<int, double> yOf,
            Func<int, double> X, Brush brush, double thickness, double opacity)
        {
            var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.PushOpacity(opacity);
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                bool first = true;
                foreach (var i in idx)
                {
                    var pt = new Point(X(i), yOf(i));
                    if (first) { ctx.BeginFigure(pt, false, false); first = false; }
                    else ctx.LineTo(pt, true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
            dc.Pop();
        }

        private FormattedText Text(string s, Brush brush, double size)
        {
            return new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        private void DrawText(DrawingContext dc, string s, Point at, Brush brush, double size)
        {
            dc.DrawText(Text(s, brush, size), at);
        }
    }
}
