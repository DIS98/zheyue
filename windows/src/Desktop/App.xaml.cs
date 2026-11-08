using System;
using System.IO;
using System.Windows;
using ClashDeck.Core;

namespace ClashDeck
{
    public partial class App : Application
    {
        private static readonly string PrefFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClashDeck", "ui.json");

        public static bool IsDark { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += (_, args) =>
            {
                Paths.WriteLog("UI " + args.Exception);
                args.Handled = true;
                // 截图/自动化模式不能弹窗，否则会一直挂在那里
                if (Array.IndexOf(e.Args, "--capture") >= 0) return;
                MessageBox.Show("界面出错：" + args.Exception.Message + "\n\n详情见 logs\\app.log",
                    "折跃", MessageBoxButton.OK, MessageBoxImage.Warning);
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) => Paths.WriteLog("FATAL " + args.ExceptionObject);

            // --api-port：让界面连指定的后端端口。
            // 作用不是给用户用的，是让"界面验证"能在沙盒里跑 —— 否则 GUI 永远连 8787，
            // 想看一眼布局就得去动用户正在用的那个实例。
            for (int i = 0; i < e.Args.Length - 1; i++)
            {
                if (e.Args[i] != "--api-port") continue;
                if (int.TryParse(e.Args[i + 1], out var p) && p > 0 && p < 65536)
                {
                    Core.Api.BaseUrl = "http://127.0.0.1:" + p;
                    // 钉住：后续不许被 data\config.json 的 webPort 覆盖（A11）
                    Core.Api.PortPinned = true;
                }
                break;
            }

            ApplyTheme(ResolveStartupTheme());

            // 自启脚本传 "silent"：开机时不要弹窗，直接进托盘。
            // （后端 A4 把 vbs 指向 ClashDeck.exe + silent，如果这里不认，开机就会弹主窗口。）
            var silent = Array.IndexOf(e.Args, "silent") >= 0;
            if (silent) ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var win = new Views.MainWindow(e.Args);
            MainWindow = win;
            win.Show();
            if (silent) win.Hide();
        }

        /// <summary>偏好：true/false = 用户明确选过；文件不存在 = 跟随系统。</summary>
        private static bool? ReadPref()
        {
            try
            {
                if (!File.Exists(PrefFile)) return null;
                var j = new JObj(Json.Parse(File.ReadAllText(PrefFile)));
                if (!j.Has("dark")) return null;
                return j.B("dark");
            }
            catch { return null; }
        }

        private static bool SystemPrefersDark()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k?.GetValue("AppsUseLightTheme");
                    if (v is int i) return i == 0;
                }
            }
            catch { }
            return false;
        }

        private static bool ResolveStartupTheme()
        {
            var pref = ReadPref();
            return pref ?? SystemPrefersDark();
        }

        public static void ApplyTheme(bool dark)
        {
            IsDark = dark;
            var dict = new ResourceDictionary
            {
                Source = new Uri(dark ? "Theme/Dark.xaml" : "Theme/Light.xaml", UriKind.Relative),
            };
            Current.Resources.MergedDictionaries[0] = dict;
        }

        public static void ToggleTheme()
        {
            ApplyTheme(!IsDark);
            SavePref(IsDark);
            if (Current.MainWindow is Views.MainWindow mw) mw.SyncTitleBarToTheme();
        }

        private static void SavePref(bool dark)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PrefFile));
                File.WriteAllText(PrefFile, Json.Stringify(Json.Obj("dark", dark)));
            }
            catch { }
        }
    }
}
