using NarTool.Core;

namespace NarTool.Gui;

/// <summary>界面共用的小工具，避免每个面板重复写同样的代码。</summary>
internal static class UiHelpers
{
    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:F1} {units[unit]}";
    }

    public static Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 6, 8, 0),
    };

    public static TextBox MakeReadOnlyBox(string placeholder = "") => new()
    {
        ReadOnly = true,
        Dock = DockStyle.Fill,
        BackColor = SystemColors.Window,
        PlaceholderText = placeholder,
    };

    public static Button MakeButton(string text)
    {
        var button = new Button { Text = text, AutoSize = false, Width = 96, Height = 28, Margin = new Padding(4) };
        button.UseVisualStyleBackColor = true;
        return button;
    }

    /// <summary>把失败明细汇总成可复制的文本，避免弹窗塞进上百行。</summary>
    public static string FormatFailures(IReadOnlyList<EntryResult> failures, int max = 20)
    {
        var lines = new List<string> { $"失败 {failures.Count} 项：", "" };
        foreach (var failure in failures.Take(max))
        {
            lines.Add(failure.Path);
            lines.Add("  " + failure.Error);
        }
        if (failures.Count > max) lines.Add($"...其余 {failures.Count - max} 项已省略");
        return string.Join(Environment.NewLine, lines);
    }
}
