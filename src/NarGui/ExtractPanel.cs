using NarTool.Core;

namespace NarTool.Gui;

/// <summary>解包页：选归档、看条目树、设选项、开跑。</summary>
internal sealed class ExtractPanel : UserControl
{
    private readonly MainForm _shell;
    private readonly TaskRunner _runner;
    private readonly TextBox _archiveBox = UiHelpers.MakeReadOnlyBox("选择或拖入 .nar 文件");
    private readonly TextBox _outputBox = UiHelpers.MakeReadOnlyBox("选择解包输出目录");
    private readonly Button _browseArchive = UiHelpers.MakeButton("浏览...");
    private readonly Button _browseOutput = UiHelpers.MakeButton("浏览...");
    private readonly Button _startButton = UiHelpers.MakeButton("开始解包");
    private readonly Button _cancelButton = UiHelpers.MakeButton("取消");
    private readonly CheckBox _decryptModels = new() { Text = "自动解除模型加密(.mdl)", AutoSize = true, Checked = true };
    private readonly CheckBox _verify = new() { Text = "解包后校验 CRC32", AutoSize = true, Checked = true };
    private readonly CheckBox _overwrite = new() { Text = "覆盖已存在文件", AutoSize = true, Checked = true };
    private readonly NumericUpDown _threads = new() { Minimum = 1, Maximum = 32, Value = Math.Clamp(Environment.ProcessorCount / 2, 2, 8), Width = 60 };
    private readonly TextBox _filter = new() { Width = 160, PlaceholderText = "只解包匹配项，如 *.wad（留空=全部）" };
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, VirtualMode = false };
    private readonly Label _summaryLabel = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };

    private NarArchive _archive;
    private List<NarEntry> _visibleEntries = new();

    public ExtractPanel(MainForm shell)
    {
        _shell = shell;
        _runner = new TaskRunner(this);

        BuildLayout();
        WireEvents();
        UpdateControls();
    }

    public bool IsBusy => _runner.IsRunning;

    public event Action<bool> ActivityChanged;

    // ------------------------------------------------------------- 布局

    private void BuildLayout()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(12);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 7,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(UiHelpers.MakeLabel("NAR 文件："), 0, 0);
        layout.Controls.Add(_archiveBox, 1, 0);
        layout.Controls.Add(_browseArchive, 2, 0);

        layout.Controls.Add(UiHelpers.MakeLabel("输出目录："), 0, 1);
        layout.Controls.Add(_outputBox, 1, 1);
        layout.Controls.Add(_browseOutput, 2, 1);

        var optionsRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 4) };
        optionsRow.Controls.Add(_decryptModels);
        optionsRow.Controls.Add(_verify);
        optionsRow.Controls.Add(_overwrite);
        optionsRow.Controls.Add(new Label { Text = "  线程数：", AutoSize = true, Margin = new Padding(8, 5, 0, 0) });
        optionsRow.Controls.Add(_threads);
        layout.Controls.Add(optionsRow, 0, 2);
        layout.SetColumnSpan(optionsRow, 3);

        var filterRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        filterRow.Controls.Add(new Label { Text = "过滤：", AutoSize = true, Margin = new Padding(0, 5, 0, 0) });
        filterRow.Controls.Add(_filter);
        layout.Controls.Add(filterRow, 0, 3);
        layout.SetColumnSpan(filterRow, 3);

        _list.Columns.Add("路径", 420);
        _list.Columns.Add("原始大小", 100, HorizontalAlignment.Right);
        _list.Columns.Add("存储大小", 100, HorizontalAlignment.Right);
        _list.Columns.Add("存储方式", 100);
        _list.Columns.Add("修改时间", 140);
        var listHost = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
        listHost.Controls.Add(_list);
        layout.Controls.Add(listHost, 0, 4);
        layout.SetColumnSpan(listHost, 3);

        layout.Controls.Add(_summaryLabel, 0, 5);
        layout.SetColumnSpan(_summaryLabel, 3);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(_cancelButton);
        buttons.Controls.Add(_startButton);
        layout.Controls.Add(buttons, 0, 6);
        layout.SetColumnSpan(buttons, 3);

        Controls.Add(layout);
        AllowDrop = true;
    }

    private void WireEvents()
    {
        _browseArchive.Click += (_, _) => PickArchive();
        _browseOutput.Click += (_, _) => PickOutput();
        _startButton.Click += (_, _) => StartWork();
        _cancelButton.Click += (_, _) => CancelWork();
        _filter.TextChanged += (_, _) => RefreshList();

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _archiveBox.AllowDrop = true;
        _archiveBox.DragEnter += OnDragEnter;
        _archiveBox.DragDrop += OnDragDrop;
    }

    // ------------------------------------------------------------- 交互

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        string file = paths.FirstOrDefault(p => p.EndsWith(".nar", StringComparison.OrdinalIgnoreCase));
        if (file != null) LoadArchive(file);
    }

    private void PickArchive()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 NAR 归档",
            Filter = "NAR 归档|*.nar|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadArchive(dialog.FileName);
    }

    private void PickOutput()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择解包输出目录", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) _outputBox.Text = dialog.SelectedPath;
        UpdateControls();
    }

    private void LoadArchive(string path)
    {
        CloseArchive();
        try
        {
            _archive = NarArchive.Open(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法打开归档：" + Environment.NewLine + ex.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _archiveBox.Text = path;

        // 默认输出到归档同级的同名文件夹
        string suggested = Path.Combine(
            Path.GetDirectoryName(path) ?? string.Empty,
            Path.GetFileNameWithoutExtension(path) + "_解包");
        _outputBox.Text = suggested;

        _list.Items.Clear();
        _visibleEntries = _archive.Entries.ToList();

        long total = 0;
        _list.BeginUpdate();
        foreach (var entry in _visibleEntries)
        {
            total += entry.Size;
            _list.Items.Add(MakeRow(entry));
        }
        _list.EndUpdate();

        var types = _visibleEntries.GroupBy(e => e.StoreType)
            .Select(g => $"{NarFormat.TypeName(g.Key)} {g.Count()}");
        _summaryLabel.Text = $"共 {_visibleEntries.Count} 项，解包后 {UiHelpers.FormatSize(total)}；存储方式：{string.Join("，", types)}" +
                             $"，将写出 {NarManifest.FileName} 供打包时自动还原";
        UpdateControls();
    }

    private static ListViewItem MakeRow(NarEntry entry)
    {
        var item = new ListViewItem(entry.Path);
        item.SubItems.Add(UiHelpers.FormatSize(entry.Size));
        item.SubItems.Add(UiHelpers.FormatSize(entry.StoreSize));
        item.SubItems.Add(NarFormat.TypeName(entry.StoreType));
        item.SubItems.Add(entry.LastModified.ToString("yyyy-MM-dd HH:mm"));
        item.Tag = entry;
        return item;
    }

    private void RefreshList()
    {
        if (_archive == null) return;
        string filter = _filter.Text.Trim();

        _visibleEntries = string.IsNullOrEmpty(filter)
            ? _archive.Entries.ToList()
            : _archive.Entries.Where(e =>
                System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(filter, Path.GetFileName(e.Path), true))
                .ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var entry in _visibleEntries) _list.Items.Add(MakeRow(entry));
        _list.EndUpdate();

        long total = _visibleEntries.Sum(e => e.Size);
        _summaryLabel.Text = $"显示 {_visibleEntries.Count} 项，{UiHelpers.FormatSize(total)}" +
                             (_archive.Entries.Count != _visibleEntries.Count ? $"（归档共 {_archive.Entries.Count} 项）" : string.Empty);
    }

    private void UpdateControls()
    {
        bool ready = _archive != null && !string.IsNullOrWhiteSpace(_outputBox.Text) && !_runner.IsRunning;
        _startButton.Enabled = ready;
        _cancelButton.Enabled = _runner.IsRunning;
        _browseArchive.Enabled = !_runner.IsRunning;
        _browseOutput.Enabled = !_runner.IsRunning;
        _decryptModels.Enabled = !_runner.IsRunning;
        _verify.Enabled = !_runner.IsRunning;
        _overwrite.Enabled = !_runner.IsRunning;
        _threads.Enabled = !_runner.IsRunning;
    }

    // ------------------------------------------------------------- 任务

    private void StartWork()
    {
        if (_archive == null || _runner.IsRunning) return;
        if (_visibleEntries.Count == 0)
        {
            MessageBox.Show(this, "没有可解包的条目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var archive = _archive;
        var entries = _visibleEntries.ToList();
        var options = new ExtractOptions
        {
            OutputDirectory = _outputBox.Text,
            DecryptModels = _decryptModels.Checked,
            VerifyChecksum = _verify.Checked,
            Overwrite = _overwrite.Checked,
            Threads = (int)_threads.Value,
        };

        _shell.ResetProgress();
        _shell.ReportProgress(new ProgressInfo(0, 1, "正在准备解包..."));
        _runner.Run(
            (progress, token) => new NarExtractor(archive, options).Extract(entries, progress, token),
            summary => OnFinished(summary, entries.Count),
            OnFailed,
            info => _shell.ReportProgress(info),
            OnActivityChanged);
    }

    public void CancelWork() => _runner.Cancel();

    private void OnActivityChanged(bool busy)
    {
        UpdateControls();
        ActivityChanged?.Invoke(busy);
        if (busy) _shell.ReportStatus("正在解包...");
    }

    private void OnFailed(Exception ex)
    {
        MessageBox.Show(this, "解包失败：" + Environment.NewLine + ex.Message, "错误",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
        _shell.ReportStatus("解包失败");
    }

    private void OnFinished(TaskSummary summary, int requested)
    {
        UpdateControls();

        if (summary == null)
        {
            _shell.ReportStatus("已取消");
            return;
        }

        _shell.ReportStatus($"完成：成功 {summary.Succeeded}，失败 {summary.Failed}，共 {UiHelpers.FormatSize(summary.Bytes)}");

        if (summary.Failures.Count > 0)
        {
            var text = UiHelpers.FormatFailures(summary.Failures);
            MessageBox.Show(this, text, "部分文件解包失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var message = $"解包完成：{summary.Succeeded} 个文件，{UiHelpers.FormatSize(summary.Bytes)}。";
        if (summary.Cancelled) message = $"已取消，但已完成的 {summary.Succeeded} 个文件仍然有效。";
        MessageBox.Show(this, message, "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void CloseArchive()
    {
        _archive?.Dispose();
        _archive = null;
        _list.Items.Clear();
        _visibleEntries.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) CloseArchive();
        base.Dispose(disposing);
    }
}
