namespace ClashDeckSetup;

internal static class SetupProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        SetupMain.EnableDpiAwareness();

        SetupMain.SetArgs(args);
        if (SetupMain.HandleFinishUninstallRequest()) return 0;

        // 卸载走独立的小程序（安装目录里的 uninstall.exe），
        // 这里兼容一下直接带参数调用安装包的情况。
        if (SetupMain.Flag("--uninstall") || SetupMain.Flag("/uninstall"))
        {
            string dir = SetupMain.Value("--dir") ?? SetupMain.Value("/dir") ?? Installer.InstalledDir ?? SetupMain.DefaultDir;
            if (SetupMain.Flag("--force"))
            {
                Installer.PurgeDirectory(dir, deleteData: true);
                return 0;
            }
            Application.Run(new UninstallForm(dir));
            return 0;
        }

        // 静默安装：--silent --dir <路径>
        if (SetupMain.Flag("--silent") || SetupMain.Flag("/S"))
        {
            string dir = SetupMain.Value("--dir") ?? SetupMain.Value("/dir") ?? SetupMain.DefaultDir;
            var r = Installer.Install(dir, desktopShortcut: true,
                progress: (pct, msg) => Console.WriteLine($"[{pct,3}%] {msg}"));
            Console.WriteLine(r.Ok ? "INSTALL_OK " + dir : "INSTALL_FAIL " + r.Message);
            return r.Ok ? 0 : 1;
        }

        // 截图模式（开发用）：--capture out.png --step 0..3
        // 用来确认界面在真实渲染下有没有被裁切，而不是靠猜。
        if (SetupMain.Value("--capture") is string shot && !string.IsNullOrEmpty(shot))
        {
            int step = 0;
            int.TryParse(SetupMain.Value("--step") ?? "0", out step);
            using (var f = new SetupForm())
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new System.Drawing.Point(40, 40);
                f.Show();
                for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                f.JumpTo(step);
                for (int i = 0; i < 16; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }

                // 用屏幕抓取而不是 DrawToBitmap：后者对 Panel 内的子控件（按钮）会漏渲染，
                // 抓出来的图看不到底部按钮，会让我误判界面。
                f.TopMost = true;
                f.Activate();
                for (int i = 0; i < 10; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                using (var bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.CopyFromScreen(f.Left, f.Top, 0, 0, f.Size);
                    bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Png);
                }
                Log.Write($"capture step={step} -> {shot} ({f.Width}x{f.Height})");
                Log.Write("---- 控件树 ----");
                Log.Write(DumpControls(f));
            }
            return 0;
        }

        Application.Run(new SetupForm());
        return 0;
    }

    /// <summary>把控件树导进日志：位置、尺寸、可见性一目了然，用来排查"界面上看不见"的问题。</summary>
    private static string DumpControls(Control root)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(Control c, int depth)
        {
            sb.AppendLine(new string(' ', depth * 2)
                + $"{c.GetType().Name} Bounds={c.Bounds} Visible={c.Visible} Enabled={c.Enabled}"
                + (string.IsNullOrEmpty(c.Text) ? "" : $" Text=\"{c.Text}\""));
            foreach (Control ch in c.Controls) Walk(ch, depth + 1);
        }
        Walk(root, 0);
        return sb.ToString();
    }
}
