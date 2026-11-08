using System;
using System.Globalization;
using System.Text;

namespace ClashDeck.Core
{
    /// <summary>界面上到处要用的数字/时间格式化，统一放这里，保证各页面口径一致。</summary>
    public static class Fmt
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

        /// <summary>字节 → 人类可读，如 1.32 GB。用 1024 进制（和订阅商给的口径一致）。</summary>
        public static string Bytes(double bytes)
        {
            if (bytes <= 0) return "0 B";
            int i = 0;
            double v = bytes;
            while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
            return (v >= 100 ? v.ToString("0", CultureInfo.InvariantCulture)
                    : v >= 10 ? v.ToString("0.#", CultureInfo.InvariantCulture)
                    : v.ToString("0.##", CultureInfo.InvariantCulture)) + " " + Units[i];
        }

        /// <summary>速率 → 如 2.4 MB/s。</summary>
        public static string Speed(double bytesPerSec) => Bytes(bytesPerSec) + "/s";

        /// <summary>
        /// 延迟 → 三态显示。
        /// 0 表示**从未测过**（刚启动、还没点测速），-1 表示测过但失败。
        /// 以前把两者都写成"超时"，后果是刚打开软件时每个节点都显示"超时"，
        /// 用户第一眼就以为节点全挂了 —— 界面在说谎。
        /// </summary>
        public static string Delay(double ms)
            => ms < 0 ? "超时" : ms == 0 ? "—" : Math.Round(ms).ToString("0") + "ms";

        /// <summary>秒 → 如 3 天 4 小时 / 12 分 30 秒。</summary>
        public static string Uptime(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (t.TotalDays >= 1) return $"{(int)t.TotalDays} 天 {t.Hours} 小时";
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours} 小时 {t.Minutes} 分";
            if (t.TotalMinutes >= 1) return $"{t.Minutes} 分 {t.Seconds} 秒";
            return $"{t.Seconds} 秒";
        }

        /// <summary>时间戳（毫秒） → “3 分钟前”。</summary>
        public static string Ago(double ms)
        {
            if (ms <= 0) return "—";
            var dt = DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime;
            var span = DateTime.Now - dt;
            if (span.TotalSeconds < 60) return "刚刚";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} 小时前";
            if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
            return dt.ToString("yyyy-MM-dd");
        }

        /// <summary>订阅到期 → “126 天后到期”。</summary>
        public static string Expire(double unixSeconds)
        {
            if (unixSeconds <= 0) return "";
            var days = Math.Ceiling((unixSeconds * 1000 - DateTimeOffset.Now.ToUnixTimeMilliseconds()) / 86400000.0);
            if (days < 0) return "已过期";
            if (days == 0) return "今天到期";
            return $"{(int)days} 天后到期";
        }

        /// <summary>
    /// 把国旗 emoji（区域指示符对）换成两位国家代码。
    /// Windows 不带国旗 emoji 字体，🇯🇵 会渲染成两个小字母 "jp"，既不好看也不好搜；
    /// 换成 "JP " 之后和节点名其余部分连读才通顺。
    /// </summary>
    public static string NormalizeFlags(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";

        var sb = new StringBuilder(s.Length + 4);
        int i = 0;
        while (i < s.Length)
        {
            int cp;
            int len;

            // 必须先判代理对再取码点：ConvertToUtf32 遇到孤立代理项会抛异常，
            // 而这里是被数据绑定反复调用的，抛一次整段文字就永久空白。
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cp = char.ConvertToUtf32(s[i], s[i + 1]);
                len = 2;
            }
            else if (char.IsSurrogate(s[i]))
            {
                sb.Append(s[i]);   // 孤立代理项：原样写出，绝不抛
                i++;
                continue;
            }
            else
            {
                cp = s[i];
                len = 1;
            }

            bool isRegional = cp >= 0x1F1E6 && cp <= 0x1F1FF && len == 2;
            if (isRegional && i + 3 < s.Length
                && char.IsHighSurrogate(s[i + 2]) && char.IsLowSurrogate(s[i + 3]))
            {
                int cp2 = char.ConvertToUtf32(s[i + 2], s[i + 3]);
                if (cp2 >= 0x1F1E6 && cp2 <= 0x1F1FF)
                {
                    sb.Append((char)('A' + (cp - 0x1F1E6)));
                    sb.Append((char)('A' + (cp2 - 0x1F1E6)));
                    i += 4;
                    if (i < s.Length && s[i] != ' ') sb.Append(' ');
                    continue;
                }
            }

            sb.Append(s, i, len);
            i += len;
        }
        return sb.ToString();
    }


    /// <summary>本地时间 HH:mm:ss。</summary>
        public static string Clock() => DateTime.Now.ToString("HH:mm:ss");
    }

    /// <summary>延迟分档，界面据此上色（快绿 / 中橙 / 慢红）。</summary>
    public enum DelayTier { None, Good, Mid, Bad }

    public static class Delay
    {
        public static DelayTier Of(double ms)
        {
            if (ms < 0) return DelayTier.Bad;    // 测过，失败 → 红
            if (ms == 0) return DelayTier.None;  // 从未测过 → 灰，不要染成失败色
            if (ms < 200) return DelayTier.Good;
            if (ms < 500) return DelayTier.Mid;
            return DelayTier.Bad;
        }

        /// <summary>把分档翻成主题里的画刷 key，XAML 里用 DynamicResource 拿不到运行时值，所以只在代码里用。</summary>
        public static string BrushKey(DelayTier t)
        {
            switch (t)
            {
                case DelayTier.Good: return "Ok";
                case DelayTier.Mid: return "Warn";
                case DelayTier.Bad: return "Bad";
                default: return "Fg3";
            }
        }

        public static string SoftKey(DelayTier t)
        {
            switch (t)
            {
                case DelayTier.Good: return "OkSoft";
                case DelayTier.Mid: return "WarnSoft";
                case DelayTier.Bad: return "BadSoft";
                default: return "Panel2";
            }
        }
    }
}
