namespace NarTool.Core;

/// <summary>单项任务的进度快照。Current 与 Total 都按条目数计。</summary>
public readonly record struct ProgressInfo(int Current, int Total, string Message)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Current / Total, 0, 1);
    public int Percent => (int)(Fraction * 100);
}

/// <summary>单个条目的处理结果，失败时携带异常信息以便汇总成报告。</summary>
public sealed record EntryResult(string Path, bool Success, long Bytes, string Error)
{
    public static EntryResult Ok(string path, long bytes) => new(path, true, bytes, null);
    public static EntryResult Fail(string path, string error) => new(path, false, 0, error);
}

/// <summary>一次解包/打包任务的汇总。多线程并发写入，计数使用原子操作。</summary>
public sealed class TaskSummary
{
    private readonly List<EntryResult> _failures = new();
    private readonly object _failureLock = new();
    private int _succeeded;
    private long _bytes;

    public IReadOnlyList<EntryResult> Failures
    {
        get { lock (_failureLock) return _failures.ToList(); }
    }

    public int Total { get; set; }

    public int Succeeded => _succeeded;

    public long Bytes => _bytes;

    public bool Cancelled { get; set; }

    public int Failed => Failures.Count;

    public void Add(EntryResult result)
    {
        if (result.Success)
        {
            Interlocked.Increment(ref _succeeded);
        }
        else
        {
            lock (_failureLock) _failures.Add(result);
        }
        Interlocked.Add(ref _bytes, result.Bytes);
    }
}
