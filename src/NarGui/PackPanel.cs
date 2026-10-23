using NarTool.Core;

namespace NarTool.Gui;

/// <summary>打包页：选源目录、定归档根与存储方式、开跑。</summary>
internal sealed class PackPanel : UserControl
{
    private readonly MainForm _shell;
    private readonly TaskRunner _runner;
    private readonly TextBox _sourceBox = UiHelpers.MakeReadOnlyBox("选择要打包的目录");
    private readonly TextBox _outputBox = UiHelpers.MakeReadOnlyBox("输出 .nar 路径");
    private readonly Button _browseSource = UiHelpers.MakeButton("浏览...");
    private readonly Button _saveOutput = UiHelpers.MakeButton("另存为...");
    private readonly Button _startButton = UiHelpers.MakeButton("开始打包");
    private readonly Button _cancelButton = UiHelpers.MakeButton("取消");
    private readonly TextBox _rootName = new() { Width = 160, PlaceholderText = "留空=自动" };
    private readonly ComboBox _storeType = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly CheckBox _encryptModels = new() { Text = "还原模型加密状态", AutoSize = true, Checked = true };
    private readonly Label _manifestLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(12, 5, 0, 0) };
    private readonly Label _summaryLabel = new() { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly TextBox _preview = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        BackColor = SystemColors.Window,
        Font = new Font("Consolas", 9F),
        WordWrap = false,
    };

    public PackPanel(MainForm shell)
    {
        _shell = shell;
        _runner = new TaskRunner(this);

        BuildLayout();
        WireEvents();
        UpdateControls();
    }

    public bool IsBusy => _runner.IsRunning;

    public event Action<bool> ActivityChanged;

    private void BuildLayout()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(12);

        _storeType.Items.AddRange(new object[]
        {
            "编码 + 压缩（推荐）",
            "仅编码（不压缩）",
            "原始（不编码不压缩）",
        });
        _storeType.SelectedIndex = 0;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 6,
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

        layout.Controls.Add(UiHelpers.MakeLabel("源目录："), 0, 0);
        layout.Controls.Add(_sourceBox, 1, 0);
        layout.Controls.Add(_browseSource, 2, 0);

        layout.Controls.Add(UiHelpers.MakeLabel("输出文件："), 0, 1);
        layout.Controls.Add(_outputBox, 1, 1);
        layout.Controls.Add(_saveOutput, 2, 1);

        var optionsRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 4) };
        optionsRow.Controls.Add(_encryptModels);
        optionsRow.Controls.Add(new Label { Text = "  归档根（可留空）：", AutoSize = true, Margin = new Padding(8, 5, 0, 0) });
        optionsRow.Controls.Add(_rootName);
        optionsRow.Controls.Add(new Label { Text = "  存储方式：", AutoSize = true, Margin = new Padding(8, 5, 0, 0) });
        optionsRow.Controls.Add(_storeType);
        optionsRow.Controls.Add(_manifestLabel);
        layout.Controls.Add(optionsRow, 0, 2);
        layout.SetColumnSpan(optionsRow, 3);

        var hintRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        hintRow.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "解包时已记录原始路径、存储方式与模型加密版本，这里全部留空即可自动还原。",
            Margin = new Padding(0, 3, 0, 0),
        });
        layout.Controls.Add(hintRow, 0, 3);
        layout.SetColumnSpan(hintRow, 3);

        var previewHost = new GroupBox { Text = "预览（前 200 个文件的归档路径）", Dock = DockStyle.Fill, Padding = new Padding(8) };
        previewHost.Controls.Add(_preview);
        layout.Controls.Add(previewHost, 0, 4);
        layout.SetColumnSpan(previewHost, 3);

        layout.Controls.Add(_summaryLabel, 0, 5);
        layout.SetColumnSpan(_summaryLabel, 3);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(_cancelButton);
        buttons.Controls.Add(_startButton);
        layout.Controls.Add(buttons, 0, 6);
        layout.SetColumnSpan(buttons, 3);
        layout.RowCount = 7;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Controls.Add(layout);
        AllowDrop = true;
    }

    private void WireEvents()
    {
        _browseSource.Click += (_, _) => PickSource();
        _saveOutput.Click += (_, _) => PickOutput();
        _startButton.Click += (_, _) => StartWork();
        _cancelButton.Click += (_, _) => CancelWork();
        _rootName.TextChanged += (_, _) => RefreshPreview();

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _sourceBox.AllowDrop = true;
        _sourceBox.DragEnter += OnDragEnter;
        _sourceBox.DragDrop += OnDragDrop;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        string directory = paths.FirstOrDefault(Directory.Exists);
        if (directory != null) LoadSource(directory);
    }

    private void PickSource()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择要打包的目录", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadSource(dialog.SelectedPath);
    }

    private void PickOutput()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "输出 NAR 归档",
            Filter = "NAR 归档|*.nar",
            FileName = string.IsNullOrWhiteSpace(_sourceBox.Text)
                ? "output.nar"
                : new DirectoryInfo(_sourceBox.Text).Name + ".nar",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _outputBox.Text = dialog.FileName;
        UpdateControls();
    }

    private void LoadSource(string directory)
    {
        _sourceBox.Text = directory;
        if (string.IsNullOrWhiteSpace(_outputBox.Text))
        {
            _outputBox.Text = Path.Combine(
                Path.GetDirectoryName(directory.TrimEnd('\\', '/')) ?? directory,
                new DirectoryInfo(directory).Name + ".nar");
        }

        UpdateManifestHint(directory);
        RefreshPreview();
        UpdateControls();
    }

    private void RefreshPreview()
    {
        string source = _sourceBox.Text;
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            _preview.Text = string.Empty;
            _summaryLabel.Text = string.Empty;
            return;
        }

        try
        {
            var manifest = NarManifest.FindNear(source);
            var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFileName(f), NarManifest.FileName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            long total = 0;
            int hit = 0;
            var lines = new List<string>(Math.Min(files.Count, 200) + 2);
            string root = _rootName.Text.Trim().Trim('/');

            foreach (string file in files.Take(200))
            {
                string relative = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
                string archivePath = null;

                if (manifest != null && manifest.TryResolve(relative, out var known))
                {
                    archivePath = known.Path;
                    hit++;
                }

                archivePath ??= "/" + (root.Length > 0 ? root + "/" : string.Empty) + relative;
                lines.Add(archivePath);
            }
            foreach (string file in files) total += new FileInfo(file).Length;

            _preview.Text = string.Join(Environment.NewLine, lines);
            _summaryLabel.Text = $"共 {files.Count} 个文件，原始 {UiHelpers.FormatSize(total)}" +
                                 (manifest != null ? $"，按清单还原 {hit} 项" : "，无清单，使用默认设置") +
                                 (files.Count > 200 ? "（预览只列前 200 项）" : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _preview.Text = string.Empty;
            _summaryLabel.Text = "读取目录失败：" + ex.Message;
        }
    }

    /// <summary>显示源目录是否带有解包清单，让用户知道能否全自动还原。</summary>
    private void UpdateManifestHint(string directory)
    {
        var manifest = NarManifest.FindNear(directory);
        if (manifest == null)
        {
            _manifestLabel.Text = "（未找到 .nar-info.json：将按默认存储方式打包）";
            return;
        }

        int encrypted = manifest.Entries.Count(e => e.Mdl is 20 or 21);
        int raw = manifest.Entries.Count(e => e.Store == (int)NarStoreType.Raw);
        _manifestLabel.Text = $"（已读取清单：{manifest.Entries.Count} 项" +
                              (encrypted > 0 ? $"，含 {encrypted} 个加密模型" : string.Empty) +
                              (raw > 0 ? $"，{raw} 项原始存储" : string.Empty) + "）";
    }

    private void UpdateControls()
    {
        bool ready = !_runner.IsRunning
                     && !string.IsNullOrWhiteSpace(_sourceBox.Text)
                     && Directory.Exists(_sourceBox.Text)
                     && !string.IsNullOrWhiteSpace(_outputBox.Text);

        _startButton.Enabled = ready;
        _cancelButton.Enabled = _runner.IsRunning;
        _browseSource.Enabled = !_runner.IsRunning;
        _saveOutput.Enabled = !_runner.IsRunning;
        _rootName.Enabled = !_runner.IsRunning;
        _storeType.Enabled = !_runner.IsRunning;
        _encryptModels.Enabled = !_runner.IsRunning;
    }

    private NarStoreType SelectedStoreType() => _storeType.SelectedIndex switch
    {
        1 => NarStoreType.Encoded,
        2 => NarStoreType.Raw,
        _ => NarStoreType.EncodedAndCompressed,
    };

    private void StartWork()
    {
        if (_runner.IsRunning) return;

        string source = _sourceBox.Text;
        string output = _outputBox.Text;

        if (File.Exists(output))
        {
            var answer = MessageBox.Show(this, "输出文件已存在，是否覆盖？" + Environment.NewLine + output,
                "确认覆盖", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
        }

        var options = new PackOptions
        {
            SourceDirectory = source,
            ArchiveRoot = _rootName.Text.Trim(),
            StoreType = SelectedStoreType(),
            RestoreModelEncryption = _encryptModels.Checked,
        };

        _shell.ResetProgress();
        _runner.Run(
            (progress, token) => new NarPacker(options).Pack(output, progress, token),
            summary => OnFinished(summary, output),
            OnFailed,
            info => _shell.ReportProgress(info),
            OnActivityChanged);
    }

    public void CancelWork() => _runner.Cancel();

    private void OnActivityChanged(bool busy)
    {
        UpdateControls();
        ActivityChanged?.Invoke(busy);
        if (busy) _shell.ReportStatus("正在打包...");
    }

    private void OnFailed(Exception ex)
    {
        MessageBox.Show(this, "打包失败：" + Environment.NewLine + ex.Message, "错误",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
        _shell.ReportStatus("打包失败");
    }

    private void OnFinished(TaskSummary summary, string output)
    {
        UpdateControls();

        if (summary == null)
        {
            _shell.ReportStatus("已取消");
            return;
        }

        if (summary.Failures.Count > 0)
        {
            _shell.ReportStatus($"打包中止：{summary.Failed} 个文件读取失败，未生成归档");
            MessageBox.Show(this,
                UiHelpers.FormatFailures(summary.Failures) + Environment.NewLine + Environment.NewLine +
                "为避免生成残缺归档，本次没有写出文件。请处理后重试。",
                "打包失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        long size = File.Exists(output) ? new FileInfo(output).Length : 0;
        _shell.ReportStatus($"完成：{summary.Succeeded} 个文件，输出 {UiHelpers.FormatSize(size)}");
        MessageBox.Show(this,
            $"打包完成：{summary.Succeeded} 个文件，原始 {UiHelpers.FormatSize(summary.Bytes)}，" +
            $"输出 {UiHelpers.FormatSize(size)}。" + Environment.NewLine + output,
            "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
