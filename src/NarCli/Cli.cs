using System.Diagnostics;
using NarTool.Core;

namespace NarTool.Cli;

/// <summary>
/// 命令行入口。三条命令：list / extract / pack，选项用 --key=value 形式。
/// 便于批量脚本调用，也便于自动化测试。
/// </summary>
internal static class Cli
{
    private const string Usage = """
        CSNZ-Nar解包打包工具-By：大宏 (命令行)

        用法：
          nar list <归档.nar>                       列出条目
          nar extract <归档.nar> <输出目录> [选项]    解包
          nar pack <输入目录> <输出.nar> [选项]       打包

        解包选项：
          --root=<路径>        只解包该路径前缀下的条目，例如 --root=/cstrike/models
          --threads=<n>        并发线程数（默认 CPU/2，2~8）
          --no-models          不自动解除 .mdl 的 ICE 加密（默认会解）
          --no-manifest        不写出 .nar-info.json 清单
          --verify             解包后按 CRC32 校验，不一致记为失败
          --no-overwrite       已存在同名文件时跳过
          --filter=<通配符>    只解包文件名匹配的条目，例如 --filter=*.wad

        打包选项：
          --root=<名称>        归档内根路径；留空则沿用解包清单里的原始路径
          --type=<raw|enc|enc+lz>  未命中清单时的默认存储方式（默认 enc+lz）
          --no-models          不还原模型的加密状态
          --no-manifest        忽略清单，所有文件用统一的存储方式

        自动化说明：
          解包时会在输出目录写出 .nar-info.json，记录归档根、每个条目的存储
          方式、以及模型原本的加密版本。打包时自动读回这些信息，因此
          "解包 → 改文件 → 打包"不需要手动指定 --root、--type 或 --models，
          模型也会自动加密回原始的 20/21 版本。

        示例：
          nar extract data.nar out
          nar pack out new.nar

        本工具全程流式处理，2 GB 以上归档也不会占满内存。
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        var options = ParseOptions(args.Skip(2));
        return args[0].ToLowerInvariant() switch
        {
            "list" => List(args, options),
            "extract" => Extract(args, options),
            "pack" => Pack(args, options),
            _ => Unknown(args[0]),
        };
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}。可用命令：list / extract / pack。");
        return 1;
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string arg in args)
        {
            if (!arg.StartsWith("--")) continue;
            string body = arg[2..];
            int separator = body.IndexOf('=');
            if (separator < 0) options[body] = "true";
            else options[body[..separator]] = body[(separator + 1)..];
        }
        return options;
    }

    private static bool Flag(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string value) &&
        (value.Length == 0 || value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("1", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ list

    private static int List(string[] args, Dictionary<string, string> options)
    {
        if (args.Length < 2) return Missing("list <归档.nar>");

        var watch = Stopwatch.StartNew();
        using var archive = NarArchive.Open(args[1]);
        Console.WriteLine($"归档：{archive.FilePath}");
        Console.WriteLine($"条目：{archive.Entries.Count}    目录表：{FormatSize(archive.TableSize)}    耗时：{watch.ElapsedMilliseconds} ms");

        foreach (var group in archive.Entries.GroupBy(e => e.StoreType).OrderBy(g => g.Key))
            Console.WriteLine($"  {NarFormat.TypeName(group.Key)}：{group.Count()} 项");

        long total = archive.Entries.Sum(e => e.Size);
        Console.WriteLine($"解包后总大小：{FormatSize(total)}");
        Console.WriteLine();

        string filter = options.GetValueOrDefault("filter");
        foreach (var entry in archive.Entries)
        {
            if (filter != null && !Matches(entry.Path, filter)) continue;
            Console.WriteLine($"{FormatSize(entry.Size),12}  {NarFormat.TypeName(entry.StoreType),-10}  {entry.LastModified:yyyy-MM-dd HH:mm}  {entry.Path}");
        }
        return 0;
    }

    // --------------------------------------------------------------- extract

    private static int Extract(string[] args, Dictionary<string, string> options)
    {
        if (args.Length < 3) return Missing("extract <归档.nar> <输出目录>");

        var watch = Stopwatch.StartNew();
        using var archive = NarArchive.Open(args[1]);

        IEnumerable<NarEntry> selected = archive.Entries;
        string root = options.GetValueOrDefault("root");
        if (!string.IsNullOrWhiteSpace(root))
        {
            string prefix = NarBuilder.NormalizePath(root);
            selected = selected.Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                           || e.Path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
        }

        string filter = options.GetValueOrDefault("filter");
        if (!string.IsNullOrWhiteSpace(filter))
            selected = selected.Where(e => Matches(e.Path, filter));

        var chosen = selected.ToList();
        if (chosen.Count == 0)
        {
            Console.Error.WriteLine("没有匹配的条目。");
            return 1;
        }

        var extractOptions = new ExtractOptions
        {
            OutputDirectory = args[2],
            DecryptModels = !Flag(options, "no-models"),
            VerifyChecksum = Flag(options, "verify"),
            WriteManifest = !Flag(options, "no-manifest"),
            Overwrite = !Flag(options, "no-overwrite"),
            Threads = ParseInt(options.GetValueOrDefault("threads"), 0),
        };

        Console.WriteLine($"解包 {chosen.Count} 项到 {Path.GetFullPath(extractOptions.OutputDirectory)}（线程 {extractOptions.Threads}）");
        var summary = new NarExtractor(archive, extractOptions).Extract(chosen, new ConsoleProgress());

        Console.WriteLine();
        Console.WriteLine($"完成：成功 {summary.Succeeded}，失败 {summary.Failed}，共 {FormatSize(summary.Bytes)}，耗时 {watch.Elapsed.TotalSeconds:F1} 秒");
        if (summary.Failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败明细：");
            foreach (var failure in summary.Failures.Take(30))
                Console.WriteLine($"  {failure.Path}\n    {failure.Error}");
            if (summary.Failures.Count > 30)
                Console.WriteLine($"  ...其余 {summary.Failures.Count - 30} 项略");
            return 1;
        }
        return 0;
    }

    // ------------------------------------------------------------------ pack

    private static int Pack(string[] args, Dictionary<string, string> options)
    {
        if (args.Length < 3) return Missing("pack <输入目录> <输出.nar>");

        var watch = Stopwatch.StartNew();
        var packOptions = new PackOptions
        {
            SourceDirectory = args[1],
            ArchiveRoot = options.GetValueOrDefault("root"),
            StoreType = ParseStoreType(options.GetValueOrDefault("type")),
            RestoreModelEncryption = !Flag(options, "no-models"),
            UseManifest = !Flag(options, "no-manifest"),
            TempDirectory = options.GetValueOrDefault("temp"),
        };

        Console.WriteLine($"打包 {Path.GetFullPath(packOptions.SourceDirectory)} → {Path.GetFullPath(args[2])}");
        var packer = new NarPacker(packOptions);
        var summary = packer.Pack(args[2], new ConsoleProgress());

        if (packer.Manifest != null)
            Console.WriteLine($"已按清单还原 {packer.ManifestHits}/{summary.Total} 个条目的原始路径与存储方式" +
                              $"（来源：{packer.Manifest.Source}）");
        else
            Console.WriteLine($"未找到 .nar-info.json，使用默认存储方式 {NarFormat.TypeName(packOptions.StoreType)}" +
                              (string.IsNullOrWhiteSpace(packOptions.ArchiveRoot) ? "，归档内路径直接取相对路径" : ""));

        Console.WriteLine();
        Console.WriteLine($"完成：成功 {summary.Succeeded}，失败 {summary.Failed}，原始共 {FormatSize(summary.Bytes)}，耗时 {watch.Elapsed.TotalSeconds:F1} 秒");
        if (summary.Failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败明细：");
            foreach (var failure in summary.Failures.Take(30))
                Console.WriteLine($"  {failure.Path}\n    {failure.Error}");
            return 1;
        }

        var info = new FileInfo(args[2]);
        if (info.Exists) Console.WriteLine($"输出文件：{info.FullName}（{FormatSize(info.Length)}）");
        return 0;
    }

    // ----------------------------------------------------------------- utils

    private static int Missing(string usage)
    {
        Console.Error.WriteLine("参数不足。用法：nar " + usage);
        return 1;
    }

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, out int parsed) && parsed > 0 ? parsed : fallback == 0 ? Math.Clamp(Environment.ProcessorCount / 2, 2, 8) : fallback;

    private static NarStoreType ParseStoreType(string value) => (value ?? string.Empty).ToLowerInvariant() switch
    {
        "" or "enc+lz" or "compressed" or "2" => NarStoreType.EncodedAndCompressed,
        "enc" or "encoded" or "1" => NarStoreType.Encoded,
        "raw" or "0" => NarStoreType.Raw,
        _ => throw new ArgumentException($"未知的存储方式：{value}（可用 raw / enc / enc+lz）。"),
    };

    private static bool Matches(string path, string pattern) =>
        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(path), ignoreCase: true);

    internal static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:F1} {units[unit]}";
    }
}

/// <summary>单行刷新的控制台进度条，重定向到文件时自动退化为普通输出。</summary>
internal sealed class ConsoleProgress : IProgress<ProgressInfo>
{
    private readonly Stopwatch _throttle = Stopwatch.StartNew();
    private int _lastPercent = -1;

    public void Report(ProgressInfo value)
    {
        if (Console.IsOutputRedirected)
        {
            if (value.Current == value.Total || value.Current % 100 == 0)
                Console.WriteLine($"[{value.Current}/{value.Total}] {value.Message}");
            return;
        }

        if (value.Percent == _lastPercent && value.Current != value.Total) return;
        _lastPercent = value.Percent;

        string bar = new string('#', value.Percent / 4).PadRight(25);
        Console.Write($"\r[{bar}] {value.Percent,3}%  {value.Current}/{value.Total}");
        if (value.Current == value.Total) Console.WriteLine();
    }
}
