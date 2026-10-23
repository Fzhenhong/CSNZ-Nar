using NarTool.Core;

namespace NarTool.Gui;

/// <summary>
/// 在后台线程执行长任务，把进度封送回 UI 线程，并统一处理取消与异常。
/// 同一时刻只允许一个任务运行，避免用户重复点击导致并发写盘。
/// </summary>
internal sealed class TaskRunner
{
    private readonly Control _owner;
    private CancellationTokenSource _cancellation;

    public TaskRunner(Control owner) => _owner = owner;

    public bool IsRunning { get; private set; }

    public void Cancel() => _cancellation?.Cancel();

    /// <summary>
    /// 执行 <paramref name="work"/>。它接收进度回调与取消令牌；
    /// <paramref name="onDone"/>、<paramref name="onError"/> 在 UI 线程被调用。
    /// </summary>
    public async void Run<T>(
        Func<IProgress<ProgressInfo>, CancellationToken, T> work,
        Action<T> onDone,
        Action<Exception> onError,
        Action<ProgressInfo> onProgress,
        Action<bool> onStateChanged)
    {
        if (IsRunning) return;

        IsRunning = true;
        _cancellation = new CancellationTokenSource();
        onStateChanged?.Invoke(true);

        try
        {
            var reporter = new UiProgress(_owner, onProgress);
            T result = await Task.Run(() => work(reporter, _cancellation.Token), _cancellation.Token);
            onDone?.Invoke(result);
        }
        catch (OperationCanceledException)
        {
            onDone?.Invoke(default);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            onDone?.Invoke(default);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsRunning = false;
            onStateChanged?.Invoke(false);
        }
    }

    /// <summary>把后台进度异步投递到 UI 线程，避免阻塞工作线程。</summary>
    private sealed class UiProgress : IProgress<ProgressInfo>
    {
        private readonly Control _owner;
        private readonly Action<ProgressInfo> _handler;

        public UiProgress(Control owner, Action<ProgressInfo> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Report(ProgressInfo value)
        {
            if (_owner.IsDisposed || _handler == null) return;
            if (_owner.InvokeRequired)
            {
                try { _owner.BeginInvoke(() => _handler(value)); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }
            else
            {
                _handler(value);
            }
        }
    }
}
