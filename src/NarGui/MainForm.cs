using NarTool.Core;

namespace NarTool.Gui;

/// <summary>主窗口：左"解包"右"打包"，共用底部进度区。</summary>
internal sealed class MainForm : Form
{
    private readonly TabControl _tabs = new();
    private readonly ExtractPanel _extract;
    private readonly PackPanel _pack;
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusText = new();
    private readonly ToolStripProgressBar _progressBar = new();
    private readonly ToolStripStatusLabel _percentText = new();

    public MainForm()
    {
        Text = "CSNZ-Nar解包打包工具-By：大宏";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 620);
        Size = new Size(980, 700);
        Font = new Font("Microsoft YaHei UI", 9F);

        _extract = new ExtractPanel(this);
        _pack = new PackPanel(this);

        var extractTab = new TabPage("解包 NAR");
        extractTab.Controls.Add(_extract);
        var packTab = new TabPage("打包 NAR");
        packTab.Controls.Add(_pack);

        _tabs.Dock = DockStyle.Fill;
        _tabs.Padding = new Point(14, 6);
        _tabs.TabPages.Add(extractTab);
        _tabs.TabPages.Add(packTab);

        BuildStatusStrip();
        Controls.Add(_tabs);
        Controls.Add(_status);

        _extract.ActivityChanged += OnActivityChanged;
        _pack.ActivityChanged += OnActivityChanged;
    }

    private void BuildStatusStrip()
    {
        _statusText.Spring = true;
        _statusText.TextAlign = ContentAlignment.MiddleLeft;
        _statusText.Text = "就绪";

        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Width = 220;
        _progressBar.Maximum = 100;

        _percentText.TextAlign = ContentAlignment.MiddleRight;
        _percentText.AutoSize = false;
        _percentText.Width = 60;
        _percentText.Text = "0%";

        _status.Items.Add(_statusText);
        _status.Items.Add(_progressBar);
        _status.Items.Add(_percentText);
        _status.SizingGrip = false;
    }

    /// <summary>子面板回报进度时更新状态栏；busy 为 true 时禁止切换标签页。</summary>
    public void ReportProgress(ProgressInfo info)
    {
        _progressBar.Value = Math.Clamp(info.Percent, 0, 100);
        _percentText.Text = info.Percent + "%";
        _statusText.Text = $"[{info.Current}/{info.Total}] {info.Message}";
    }

    public void ReportStatus(string text)
    {
        _statusText.Text = text;
    }

    public void ResetProgress()
    {
        _progressBar.Value = 0;
        _percentText.Text = "0%";
    }

    private void OnActivityChanged(bool busy)
    {
        _tabs.Enabled = !busy;
        Cursor = busy ? Cursors.AppStarting : Cursors.Default;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_extract.IsBusy || _pack.IsBusy)
        {
            var answer = MessageBox.Show(this, "任务正在运行，确定要中止并退出吗？", "确认退出",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _extract.CancelWork();
            _pack.CancelWork();
        }
        base.OnFormClosing(e);
    }
}
