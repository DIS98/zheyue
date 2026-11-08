using Microsoft.Win32;

namespace ClashDeckSetup;

/// <summary>
/// 轻量卸载器（安装目录里的 uninstall.exe）。
/// 不带参数直接双击 = 卸载当前安装；--force 由卸载流程自己延迟调用。
/// </summary>
internal static class UninstallerProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        SetupMain.EnableDpiAwareness();

        SetupMain.SetArgs(args);
        if (SetupMain.HandleFinishUninstallRequest()) return 0;

        // 没给路径时：先用注册表记的安装位置，再退到自身所在目录
        string dir = SetupMain.Value("--dir") ?? SetupMain.Value("/dir")
                     ?? Installer.InstalledDir
                     ?? Path.GetDirectoryName(SetupMain.SelfPath());

        if (SetupMain.Flag("--silent") || SetupMain.Flag("/S"))
        {
            var r = Installer.Uninstall(dir, deleteData: SetupMain.Flag("--purge"));
            Console.WriteLine(r.Ok ? "UNINSTALL_OK" : "UNINSTALL_FAIL " + r.Message);
            return r.Ok ? 0 : 1;
        }

        // 截图模式（开发用）：确认卸载界面没有被裁切、按钮没飞出窗口
        if (SetupMain.Value("--capture") is string shot && !string.IsNullOrEmpty(shot))
        {
            using (var f = new UninstallForm(dir))
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new System.Drawing.Point(40, 40);
                f.Show();
                for (int i = 0; i < 16; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                f.TopMost = true;
                f.Activate();
                for (int i = 0; i < 10; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                using (var bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.CopyFromScreen(f.Left, f.Top, 0, 0, f.Size);
                    bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Png);
                }
                Log.Write("uninstall capture -> " + shot);
            }
            return 0;
        }

        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show("找不到 折跃 的安装目录。\n如果已经手动删过文件，可以到「设置 → 应用」里点一次卸载来清掉残留条目。",
                "卸载", MessageBoxButtons.OK, MessageBoxIcon.Information);
            try { Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClashDeck", false); } catch { }
            return 0;
        }

        Application.Run(new UninstallForm(dir));
        return 0;
    }
}
