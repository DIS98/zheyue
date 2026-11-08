using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace ClashDeckSetup;

internal sealed class SetupForm : Form
{
    private readonly Banner _banner;
    private readonly Panel _host;
    private readonly Panel[] _pages;
    private readonly Label _stepHint;
    private readonly FlatBtn _btnBack;
    private readonly FlatBtn _btnNext;
    private readonly FlatBtn _btnCancel;

    // 选项页
    private readonly TextBox _txtDir;
    private readonly CheckBox _chkDesktop;
    private readonly CheckBox _chkLaunch;
    private readonly Label _lblHint;

    // 进度页
    private readonly ProgressBar _bar;
    private readonly Label _lblStage;
    private readonly TextBox _txtLog;

    private int _step;
    private bool _installing;
    private string _installDir;

    public SetupForm()
    {
        Text = SetupMain.AppName + " 安装程序";
        ClientSize = new Size(760, 600);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        Font = Theme.FBody;
        AutoScaleMode = AutoScaleMode.Dpi;
        try { Icon = Icon.ExtractAssociatedIcon(SetupMain.SelfPath()); } catch { }

        _banner = new Banner { Dock = DockStyle.Top };
        try { _banner.Icon = Icon?.ToBitmap(); } catch { }

        // ---------------- 底部按钮条
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 66, BackColor = Theme.Card };
        bottom.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, bottom.Width, 0);
        };
        // 按钮栏用 FlowLayoutPanel 靠右排：早先用 Anchor.Right + 绝对坐标，
        // 父容器还没定型时算出的偏移是负数，按钮会飞到窗口外（实测 X=1206，窗口才 760 宽）。
        _btnCancel = new FlatBtn("取消");
        _btnNext = new FlatBtn("下一步", primary: true);
        _btnBack = new FlatBtn("上一步") { Visible = false };
        foreach (var b in new[] { _btnCancel, _btnNext, _btnBack })
            b.Margin = new Padding(8, 0, 0, 0);

        var btnBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(0, 16, 16, 0),
            BackColor = Color.Transparent,
        };
        btnBar.Controls.Add(_btnCancel);
        btnBar.Controls.Add(_btnNext);
        btnBar.Controls.Add(_btnBack);

        _btnCancel.Click += (_, _) => { if (!_installing) Close(); };
        _btnBack.Click += (_, _) => GoTo(_step - 1);
        _btnNext.Click += (_, _) => OnNext();
        bottom.Controls.Add(btnBar);

        // ---------------- 中间内容
        _host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(28, 22, 28, 8) };
        _stepHint = new Label
        {
            Dock = DockStyle.Bottom, Height = 22, ForeColor = Theme.Text3,
            Font = Theme.FSmall, TextAlign = ContentAlignment.MiddleLeft,
        };

        // ===== 页 0：欢迎
        var p0 = NewPage();
        var intro = new Label
        {
            Dock = DockStyle.Top, Height = 104, Font = Theme.FBody, ForeColor = Theme.Text2,
            Text = "这个安装程序会把 折跃 装到你的电脑上：一个本地代理控制台，\r\n"
                 + "用来管理 mihomo 内核 —— 起停、切节点、看规则、看流量。\r\n\r\n"
                 + "安装包是自包含的：主程序自带 .NET 运行时，内核与 geo 数据都已内置。",
        };
        var feat = new CardBox
        {
            Dock = DockStyle.Top, Height = 176, Margin = new Padding(0, 10, 0, 0),
            Title = "会装进这些",
            Items = new[]
            {
                "程序外壳（自带 .NET 运行时）+ 内置 Node，约 235 MB",
                "mihomo 内核 v1.19.32 与 geo 数据，首次启动不需要联网",
                "系统代理工具（写 HKCU，不需要管理员权限）",
                "开始菜单与桌面快捷方式，可在「设置 → 应用」里卸载",
            },
        };
        var warn = new Label
        {
            Dock = DockStyle.Top, Height = 72, ForeColor = Color.FromArgb(0x9A, 0x5B, 0x00),
            Font = Theme.FSmall, Padding = new Padding(2, 8, 0, 0),
            Text = "注意：程序占用 7897 / 9097 端口，和 Clash Verge 不能同时运行\r\n"
                 + "（要同时用，就在「配置 → 内核与端口」里改到 17897）。首次启动后\r\n"
                 + "在「配置」里导入订阅并激活，然后点右上角「启动」即可。",
        };
        p0.Controls.AddRange(new Control[] { warn, feat, intro });

        // ===== 页 1：选项
        var p1 = NewPage();
        var dirHead = new Label { Dock = DockStyle.Top, Height = 26, Text = "安装位置", Font = Theme.FHead, ForeColor = Theme.Text };
        // 用 TableLayoutPanel：输入框自适应宽度，按钮固定靠右，不再靠绝对坐标
        var dirRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            ColumnCount = 2,
            RowCount = 1,
        };
        dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112f));

        _txtDir = new TextBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 9, 0, 9),
            Font = Theme.FBody,
            BorderStyle = BorderStyle.FixedSingle,
            Text = Installer.InstalledDir ?? SetupMain.DefaultDir,
        };
        var btnBrowse = new FlatBtn("选择目录…")
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(8, 4, 0, 4),
            Width = 112,
        };
        btnBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "选择安装位置", ShowNewFolderButton = true };
            try
            {
                var cur = _txtDir.Text.Trim();
                if (!string.IsNullOrEmpty(cur))
                {
                    var parent = Path.GetDirectoryName(cur);
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) dlg.SelectedPath = parent;
                    else if (Directory.Exists(cur)) dlg.SelectedPath = cur;
                }
            }
            catch { }
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _txtDir.Text = Path.Combine(dlg.SelectedPath, SetupMain.AppName.Replace(" ", ""));
        };
        dirRow.Controls.Add(_txtDir, 0, 0);
        dirRow.Controls.Add(btnBrowse, 1, 0);

        _lblHint = new Label
        {
            Dock = DockStyle.Top, Height = 34, ForeColor = Theme.Text3, Font = Theme.FSmall,
            Text = "装到当前用户目录不需要管理员权限。换到 C:\\Program Files 请以管理员身份运行。",
        };

        var optHead = new Label { Dock = DockStyle.Top, Height = 30, Text = "选项", Font = Theme.FHead, ForeColor = Theme.Text, Padding = new Padding(0, 10, 0, 0) };
        _chkDesktop = new CheckBox
        {
            Dock = DockStyle.Top, Height = 30, Text = "在桌面创建快捷方式", Checked = true,
            Font = Theme.FBody, ForeColor = Theme.Text,
        };
        _chkLaunch = new CheckBox
        {
            Dock = DockStyle.Top, Height = 30, Text = "安装完成后立即启动 折跃", Checked = true,
            Font = Theme.FBody, ForeColor = Theme.Text,
        };
        var upgrade = new Label
        {
            Dock = DockStyle.Top, Height = 44, ForeColor = Theme.Text2, Font = Theme.FSmall, Padding = new Padding(0, 10, 0, 0),
            Text = Installer.InstalledVersion is { } v
                ? $"检测到已安装 v{v}，本次为覆盖升级：程序文件会更新，data 目录里的配置、订阅、流量统计会保留。"
                : "",
        };
        var space = new Label
        {
            Dock = DockStyle.Top, Height = 62, ForeColor = Theme.Text3, Font = Theme.FSmall,
            Padding = new Padding(0, 14, 0, 0),
            Text = "安装后约占用 235 MB（主程序 47 MB + 内核 58 MB + geo 44 MB + Node 90 MB）。\r\n"
                 + "卸载时会问你要不要保留配置；不保留就把整个目录删干净，不留系统残留。",
        };
        p1.Controls.AddRange(new Control[] { space, upgrade, _chkLaunch, _chkDesktop, optHead, _lblHint, dirRow, dirHead });

        // ===== 页 2：进度
        var p2 = NewPage();
        _lblStage = new Label
        {
            Dock = DockStyle.Top, Height = 28, Text = "准备安装…", Font = Theme.FBody, ForeColor = Theme.Text,
        };
        _bar = new ProgressBar { Dock = DockStyle.Top, Height = 8, Style = ProgressBarStyle.Continuous, Maximum = 100 };
        var barPad = new Panel { Dock = DockStyle.Top, Height = 26, Padding = new Padding(0, 6, 0, 12) };
        barPad.Controls.Add(_bar);
        _bar.Dock = DockStyle.Fill;
        _txtLog = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Font = Theme.FMono, BackColor = Color.FromArgb(0xFB, 0xFC, 0xFE), BorderStyle = BorderStyle.None,
            ForeColor = Theme.Text2, WordWrap = false,
        };
        var logPad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1) };
        logPad.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, logPad.Width - 1, logPad.Height - 1);
        };
        logPad.Controls.Add(_txtLog);
        p2.Controls.AddRange(new Control[] { logPad, barPad, _lblStage });

        // ===== 页 3：完成
        var p3 = NewPage();
        _lblDone = new Label
        {
            Dock = DockStyle.Top, Height = 44, Font = new Font(Theme.FHead.FontFamily, 15f, FontStyle.Bold),
            ForeColor = Theme.Ok, Text = "安装完成", Padding = new Padding(0, 8, 0, 0),
        };
        _lblDonePath = new Label
        {
            Dock = DockStyle.Top, Height = 60, Font = Theme.FBody, ForeColor = Theme.Text2, Padding = new Padding(0, 6, 0, 0),
            Text = "",
        };
        _lblDoneTip = new Label
        {
            Dock = DockStyle.Top, Height = 110, Font = Theme.FSmall, ForeColor = Theme.Text3,
            Text = "内核已经随包装好，不需要另外指定路径。\r\n"
                 + "第一次打开后：在「配置 → 导入订阅」粘贴订阅链接（或把 YAML 拖进窗口），\r\n"
                 + "点「激活」，再点右上角「启动」。需要浏览器走代理时，打开「系统代理」。\r\n"
                 + "关窗口不会退出，它会收进托盘继续跑；右键托盘图标才是真正退出。",
        };
        var btnOpenDir = new FlatBtn("打开安装目录") { Width = 116, Location = new Point(0, 4) };
        btnOpenDir.Click += (_, _) => OpenDir(_installDir);
        _btnInstallWv2 = new FlatBtn("下载 .NET 4.8", primary: true)
        {
            Width = 136,
            Location = new Point(126, 4),
            Visible = false,
        };
        _btnInstallWv2.Click += (_, _) => RunWebView2Setup();
        var dirRow2 = new Panel { Dock = DockStyle.Top, Height = 42 };
        dirRow2.Controls.Add(btnOpenDir);
        dirRow2.Controls.Add(_btnInstallWv2);
        p3.Controls.AddRange(new Control[] { dirRow2, _lblDoneTip, _lblDonePath, _lblDone });

        _pages = new[] { p0, p1, p2, p3 };
        foreach (var p in _pages) { p.Visible = false; _host.Controls.Add(p); }
        _host.Controls.Add(_stepHint);
        _stepHint.BringToFront();

        Controls.AddRange(new Control[] { _host, bottom, _banner });
        GoTo(0);
    }

    private Label _lblDone;
    private Label _lblDonePath;
    private Label _lblDoneTip;
    private FlatBtn _btnInstallWv2;

    private static Panel NewPage() => new()
    {
        Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false, AutoScroll = true,
    };

    /// <summary>供 --capture 截图用：直接切到指定步骤。</summary>
    public void JumpTo(int step) => GoTo(step);

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // 高 DPI 或小屏下窗体可能被放大到超出屏幕，导致底部按钮看不见。
        // 夹到工作区内并重新居中；内容装不下时由页面自己的 AutoScroll 兜底。
        try
        {
            var wa = Screen.FromControl(this).WorkingArea;
            int maxW = Math.Max(620, wa.Width - 40);
            int maxH = Math.Max(460, wa.Height - 40);
            if (Width > maxW || Height > maxH)
            {
                Width = Math.Min(Width, maxW);
                Height = Math.Min(Height, maxH);
                Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
                Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
                Log.Write($"window clamped to {Width}x{Height} (workarea {wa.Width}x{wa.Height})");
            }
        }
        catch { }
    }

    private void GoTo(int step)
    {
        _step = SetupMain.Clamp(step, 0, 3);
        for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = i == _step;

        _btnBack.Visible = _step == 1;
        _btnCancel.Visible = _step < 2;
        _btnNext.Visible = _step < 2;   // 安装过程中（step2）不该出现"下一步"
        _btnNext.Text = _step == 0 ? "开始安装" : _step == 1 ? "安装" : "下一步";
        _btnNext.Size = new Size(_step == 1 ? 108 : 108, 34);
        _stepHint.Text = _step switch
        {
            0 => "第一步 · 了解要安装的内容",
            1 => "第二步 · 选择安装位置与选项",
            2 => "第三步 · 正在安装",
            _ => "完成",
        };
        _banner.Subtitle = _step < 2 ? $"版本 {SetupMain.AppVersion} · 自用构建" : "";
        _banner.Invalidate();
    }

    private void OnNext()
    {
        if (_step == 0) { GoTo(1); return; }
        if (_step != 1) return;

        var dir = _txtDir.Text.Trim();
        if (string.IsNullOrEmpty(dir)) { MessageBox.Show(this, "请填写安装目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        try { dir = Path.GetFullPath(dir); }
        catch { MessageBox.Show(this, "路径格式不正确。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        if (File.Exists(Path.Combine(dir, SetupMain.AppExe)) && Installer.RunningApps(dir).Count > 0)
        {
            var r = MessageBox.Show(this, "折跃 正在运行，安装会先把它关掉。继续？",
                "需要关闭正在运行的程序", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r != DialogResult.OK) return;
        }

        StartInstall(dir);
    }

    private void StartInstall(string dir)
    {
        _installing = true;
        _installDir = dir;
        GoTo(2);
        _btnNext.Enabled = false;
        _btnBack.Visible = false;
        _btnCancel.Enabled = false;
        _txtLog.Clear();
        AppendLog($"安装到 {dir}");

        var desktop = _chkDesktop.Checked;
        Task.Run(() =>
        {
            var result = Installer.Install(dir, desktop, (pct, msg) =>
            {
                try
                {
                    BeginInvoke(() =>
                    {
                        _bar.Value = SetupMain.Clamp(pct, 0, 100);
                        _lblStage.Text = msg;
                        AppendLog(msg);
                    });
                }
                catch { }
            });

            try
            {
                BeginInvoke(() => Finish(result));
            }
            catch { }
        });
    }

    private void AppendLog(string msg)
    {
        _txtLog.AppendText($"{DateTime.Now:HH:mm:ss}  {msg}{Environment.NewLine}");
    }

    private void Finish(InstallResult result)
    {
        _installing = false;
        _btnCancel.Enabled = true;
        if (!result.Ok)
        {
            _lblStage.Text = "安装没有完成";
            _lblStage.ForeColor = Theme.Bad;
            AppendLog("失败：" + result.Message);
            MessageBox.Show(this, result.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _btnNext.Enabled = true;
            _btnNext.Visible = true;
            _btnNext.Text = "重试";
            _btnBack.Visible = true;
            _btnNext.Click -= RetryHandler;
            _btnNext.Click += RetryHandler;
            return;
        }

        _bar.Value = 100;
        _lblStage.Text = "安装完成";
        _lblDonePath.Text = $"安装位置：{_installDir}\r\n可以在「设置 → 应用 → 已安装的应用」里卸载。";

        // 没装 WebView2 的机器（Win10 常见）还能用，但界面会走浏览器模式，这里必须讲清楚
        if (result.Net48Missing)
        {
            _lblDoneTip.Text = "这台电脑的 .NET Framework 低于 4.8。Win10 1903 以上和 Win11 都自带 4.8。"
                + "\r\n如果程序打不开，点下面的按钮去微软官网下载安装（免费、约 110 MB），装完重新打开即可。";
            _lblDoneTip.ForeColor = Color.FromArgb(0x9A, 0x5B, 0x00);
            _btnInstallWv2.Visible = true;
            AppendLog("提示：未检测到 .NET Framework 4.8，程序可能打不开");
        }
        else
        {
            _btnInstallWv2.Visible = false;
            AppendLog(".NET Framework 4.8 已就绪");
        }

        GoTo(3);
        DisableNext();

        if (_chkLaunch.Checked)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(_installDir, SetupMain.AppExe))
                { UseShellExecute = true, WorkingDirectory = _installDir });
            }
            catch (Exception ex) { Log.Write("launch failed: " + ex.Message); }
        }
    }

    private void RetryHandler(object sender, EventArgs e)
    {
        _btnNext.Click -= RetryHandler;
        StartInstall(_txtDir.Text.Trim());
    }

    private void DisableNext()
    {
        _btnNext.Visible = false;
        _btnBack.Visible = false;
        _btnCancel.Text = "关闭";
        _btnCancel.Visible = true;
    }

    private void RunWebView2Setup()
    {
        // 不再随包附带安装器：直接把用户带到微软官方下载页（约 110 MB，需要联网）
        const string url = "https://dotnet.microsoft.com/download/dotnet-framework/net48";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            MessageBox.Show(this,
                "已在浏览器打开 .NET Framework 4.8 下载页。\r\n\r\n装完请重新打开 折跃。",
                "下载 .NET Framework 4.8", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Write("open net48 page failed: " + ex.Message);
            MessageBox.Show(this, "打开浏览器失败，请手动访问：\r\n" + url, "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void OpenDir(string dir)
    {
        try
        {
            if (string.IsNullOrEmpty(dir)) dir = _installDir;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { }
    }
}

/// <summary>带标题的要点清单卡片。</summary>
internal sealed class CardBox : Panel
{
    public string Title { get; set; } = "";
    public string[] Items { get; set; } = Array.Empty<string>();

    public CardBox()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.Round(rect, 10))
        {
            using var bg = new SolidBrush(Theme.Card);
            g.FillPath(bg, path);
            using var pen = new Pen(Theme.Border);
            g.DrawPath(pen, path);
        }

        int y = 14;
        TextRenderer.DrawText(g, Title, Theme.FHead, new Point(18, y), Theme.Text, TextFormatFlags.NoPadding);
        y += 28;

        foreach (var it in Items)
        {
            using (var dot = new SolidBrush(Theme.Brand1))
                g.FillEllipse(dot, 20, y + 6, 6, 6);
            var size = TextRenderer.MeasureText(g, it, Theme.FBody, new Size(Width - 60, 0), TextFormatFlags.WordBreak);
            TextRenderer.DrawText(g, it, Theme.FBody, new Rectangle(34, y, Width - 54, size.Height + 4),
                Theme.Text2, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            y += size.Height + 10;
        }
    }
}
