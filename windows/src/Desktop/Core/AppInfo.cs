using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace ClashDeck.Core
{
    /// <summary>
    /// 版本信息统一从这里取，界面上不再手写版本号。
    /// 以前侧栏写死 "v2.0.0"，而实际已经发到 2.0.9 —— 界面在说谎，
    /// 而且这种谎只有用户去看才发现，代码里没有任何东西会变红。
    /// </summary>
    public static class AppInfo
    {
        /// <summary>发布版本号（三段，和下载页 / 安装包版本一致），如 2.0.9</summary>
        public static string Release { get; } = ResolveRelease();

        /// <summary>完整版本号（四段，程序集版本），如 2.0.9.0</summary>
        public static string Full { get; } = ResolveFull();

        private static Version Current()
        {
            try
            {
                return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            }
            catch
            {
                return new Version(0, 0, 0, 0);
            }
        }

        private static string ResolveRelease()
        {
            var v = Current();
            // 优先用 FileVersionInfo 的产品版本（打包时由 -p:Version 注入，可能带后缀）
            try
            {
                var path = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var fvi = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrWhiteSpace(fvi.ProductVersion))
                        return fvi.ProductVersion.Trim();
                }
            }
            catch { /* 退回程序集版本 */ }
            return string.Format("{0}.{1}.{2}", v.Major, v.Minor, Math.Max(v.Build, 0));
        }

        private static string ResolveFull()
        {
            var v = Current();
            return string.Format("{0}.{1}.{2}.{3}", v.Major, v.Minor,
                Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        }
    }
}
