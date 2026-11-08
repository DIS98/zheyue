using System.Windows;
using System.Windows.Controls;

namespace ClashDeck.Core
{
    /// <summary>
    /// 把主题资源绑成 DynamicResource 的等价物。
    ///
    /// 为什么必须有它：`Foreground = Res("Fg")` 这种写法取到的是**当前那一刻的 Brush 对象**，
    /// 而切换主题是替换整个 ResourceDictionary（App.ApplyTheme），旧对象不会更新。
    /// 结果就是深色模式下这类文字仍是浅色主题的近黑色，直接"消失"在深色背景里。
    /// 用 SetResourceReference 之后，换主题时 WPF 会自动重新取。
    /// </summary>
    internal static class ThemeExt
    {
        public static TextBlock Fg(this TextBlock el, string key)
        {
            el.SetResourceReference(TextBlock.ForegroundProperty, key);
            return el;
        }

        public static Panel Bg(this Panel el, string key)
        {
            el.SetResourceReference(Panel.BackgroundProperty, key);
            return el;
        }

        public static Border BgB(this Border el, string key)
        {
            el.SetResourceReference(Border.BackgroundProperty, key);
            return el;
        }
    }
}
