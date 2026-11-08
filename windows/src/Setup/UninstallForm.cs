namespace ClashDeckSetup;

internal sealed class UninstallForm : Form
{
    private readonly string _dir;
    private readonly CheckBox _chkData;
    private readonly Label _stage;
    private readonly ProgressBar _bar;
    private readonly FlatBtn _btnGo;
    private readonly FlatBtn _btnClose;
    private bool _busy;

    public UninstallForm(string dir)
    {
        _dir = string.IsNullOrWhiteSpace(dir) ? SetupMain.DefaultDir : dir;

        Text = "卸载 " + SetupMain.AppName;
        ClientSize = new Size(620, 410);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        Font = Theme.FBody;
        AutoScaleMode = AutoScaleMode.Dpi;
        try { Icon = Icon.ExtractAssociatedIcon(SetupMain.SelfPath()); } catch { }

        var banner = new Banner
        {
            Dock = DockStyle.Top,
            Title = "卸载 " + SetupMain.AppName,
            Subtitle = Installer.InstalledVersion is { } v ? $"已安装版本 {v}" : "移除程序文件",
        };
        try { banner.Icon = Icon?.ToBitmap(); } catch { }

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(26, 20, 26, 10), BackColor = Theme.Bg };

        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 54,
            Font = Theme.FBody,
            ForeColor = Theme.Text2,
            Text = "安装位置：\r\n" + _dir,
        };

        _chkData = new CheckBox
        {
            Dock = DockStyle.Top,
            Height = 30,
            Text = "同时删除配置、订阅与流量统计（data 目录）",
            Font = Theme.FBody,
            ForeColor = Theme.Text,
            Checked = false,
        };

        var tip = new Label
        {
            Dock = DockStyle.Top,
            Height = 46,
            Font = Theme.FSmall,
            ForeColor = Theme.Text3,
            Padding = new Padding(0, 6, 0, 0),
            Text = "不勾选时只删除程序文件，配置会留在安装目录的 data 里；\r\n"
                 + "想彻底清干净就先在应用里「配置 → 导出备份」，再勾选这一项。",
        };

        _stage = new Label { Dock = DockStyle.Top, Height = 24, ForeColor = Theme.Text2, Font = Theme.FBody };
        _bar = new ProgressBar { Dock = DockStyle.Top, Height = 8, Maximum = 100, Style = ProgressBarStyle.Continuous };
        var barPad = new Panel { Dock = DockStyle.Top, Height = 24, Padding = new Padding(0, 8, 0, 8) };
        barPad.Controls.Add(_bar);
        _bar.Dock = DockStyle.Fill;

        host.Controls.AddRange(new Control[] { barPad, _stage, tip, _chkData, info });

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Theme.Card };
        bottom.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, bottom.Width, 0);
        };
        _btnClose = new FlatBtn("取消");
        _btnGo = new FlatBtn("卸载", primary: true);
        foreach (var b in new[] { _btnClose, _btnGo }) b.Margin = new Padding(8, 0, 0, 0);

        var btnBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(0, 15, 16, 0),
            BackColor = Color.Transparent,
        };
        btnBar.Controls.Add(_btnClose);
        btnBar.Controls.Add(_btnGo);

        _btnClose.Click += (_, _) => { if (!_busy) Close(); };
        _btnGo.Click += (_, _) => DoUninstall();
        bottom.Controls.Add(btnBar);

        Controls.AddRange(new Control[] { host, bottom, banner });
    }

    private void DoUninstall()
    {
        if (_busy) return;
        var deleteData = _chkData.Checked;
        var warn = deleteData
            ? "将删除程序文件，以及配置、订阅和流量统计。这个操作不可撤销，继续？"
            : "将删除程序文件，配置会保留在安装目录的 data 里。继续卸载？";
        if (MessageBox.Show(this, warn, "确认卸载", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        _busy = true;
        _btnGo.Enabled = false;
        _btnClose.Enabled = false;
        _chkData.Enabled = false;
        _stage.Text = "正在卸载…";
        _bar.Style = ProgressBarStyle.Marquee;

        Task.Run(() =>
        {
            var r = Installer.Uninstall(_dir, deleteData);
            try
            {
                BeginInvoke(() =>
                {
                    _busy = false;
                    _bar.Style = ProgressBarStyle.Continuous;
                    _bar.Value = 100;
                    if (r.Ok)
                    {
                        _stage.ForeColor = Theme.Ok;
                        _stage.Text = deleteData ? "已卸载，配置已一并删除" : "已卸载，配置保留在 data 目录";
                        MessageBox.Show(this,
                            deleteData
                                ? "折跃 已卸载。"
                                : "折跃 已卸载。\n\n配置仍然保留在：\n" + Path.Combine(_dir, "data"),
                            "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        _btnGo.Visible = false;
                        _btnClose.Text = "关闭";
                        _btnClose.Enabled = true;
                    }
                    else
                    {
                        _stage.ForeColor = Theme.Bad;
                        _stage.Text = "卸载失败";
                        MessageBox.Show(this, r.Message, "卸载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        _btnGo.Enabled = true;
                        _btnClose.Enabled = true;
                    }
                });
            }
            catch { }
        });
    }
}
