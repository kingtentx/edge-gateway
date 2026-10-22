using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using EdgeGateway.Abstractions;
using EdgeGateway.Model;

namespace EdgeGateway.IotGateway;

/// <summary>
/// Owns one physical thread for a synchronous driver. On Windows it is STA and pumps window/COM messages.
/// Cancellation quarantines the instance. Cleanup runs on that same thread after the native call returns;
/// an unfinished native call is never aborted or raced by Close on another thread.
/// </summary>
internal sealed class DedicatedDriverExecutor
{
    private readonly BlockingCollection<Action> _operations = new();
    private readonly object _lifecycle = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly Action _cleanup;
    private readonly IGatewayLog _log;
    private readonly Action? _onFinished;
    private int _poisoned;
    private int _stopping;
    private readonly WorkerSynchronizationContext _context = new();

    public DedicatedDriverExecutor(string name, Action cleanup, IGatewayLog log, Action? onFinished = null)
    {
        _cleanup = cleanup;
        _log = log;
        _onFinished = onFinished;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public bool IsFinished => _finished.Task.IsCompleted;
    public bool IsQuarantined => Volatile.Read(ref _poisoned) != 0;

    public async Task<T> InvokeAsync<T>(Func<T> operation, int timeoutMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycle)
        {
            if (Volatile.Read(ref _stopping) != 0 || IsQuarantined)
                throw new InvalidOperationException("此驱动实例已停止或隔离，不能复用。");
            _operations.Add(() =>
            {
                if (IsQuarantined) { completion.TrySetException(new InvalidOperationException("操作已取消，驱动正在隔离清理。")); return; }
                try { completion.TrySetResult(operation()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        try { return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!completion.Task.IsCompleted) Quarantine();
            _ = completion.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("驱动调用已取消；未完成操作已隔离，写入结果需读回核实。", cancellationToken);
            throw new TimeoutException($"驱动调用超过 {timeoutMs} ms；旧实例已隔离，待原生调用退出后释放，不会并发复用。");
        }
    }

    public T RunTask<T>(Func<Task<T>> operation)
    {
        if (Thread.CurrentThread != _thread) throw new InvalidOperationException("异步原驱动调用必须在专属线程发起。");
        var task = operation();
        while (!task.IsCompleted)
        {
            _context.Drain();
            PumpMessages();
            _context.Wait(10);
        }
        _context.Drain();
        return task.GetAwaiter().GetResult();
    }

    public void Quarantine()
    {
        Interlocked.Exchange(ref _poisoned, 1);
        RequestStop();
    }

    public async ValueTask StopAsync(int graceMs = 500)
    {
        RequestStop();
        try { await _finished.Task.WaitAsync(TimeSpan.FromMilliseconds(graceMs)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            Interlocked.Exchange(ref _poisoned, 1);
            _log.Write(GatewayLogLevel.Warning, $"驱动线程 [{_thread.Name}] 的原生调用尚未退出，已隔离；同设备暂不创建新会话，原调用结束后将在原线程关闭并释放。");
        }
    }

    private void RequestStop()
    {
        lock (_lifecycle)
            if (Interlocked.Exchange(ref _stopping, 1) == 0) _operations.CompleteAdding();
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(_context);
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        try
        {
            while (!_operations.IsCompleted)
            {
                if (_operations.TryTake(out var operation, 20)) operation();
                _context.Drain();
                PumpMessages();
            }
        }
        catch (Exception ex)
        {
            Quarantine();
            _log.Write(GatewayLogLevel.Error, $"驱动专属线程异常：{ex.GetBaseException().Message}");
            // Complete accepted queued callers with an error without executing their driver operations.
            while (_operations.TryTake(out var pending)) pending();
        }
        finally
        {
            try { _cleanup(); }
            catch (Exception ex) { _log.Write(GatewayLogLevel.Warning, $"驱动清理失败：{ex.GetBaseException().Message}"); }
            finally
            {
                try { _context.Drain(); }
                catch (Exception ex) { _log.Write(GatewayLogLevel.Warning, $"驱动退出回调异常：{ex.GetBaseException().Message}"); }
                finally
                {
                    lock (_lifecycle)
                    {
                        Interlocked.Exchange(ref _stopping, 1);
                        _operations.Dispose();
                    }
                    _context.Dispose();
                    SynchronizationContext.SetSynchronizationContext(null);
                    try { _onFinished?.Invoke(); }
                    finally { _finished.TrySetResult(); }
                }
            }
        }
    }

    private static void PumpMessages()
    {
        if (!OperatingSystem.IsWindows()) return;
        while (PeekMessage(out var message, 0, 0, 0, 1))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage { public nint Window; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X; public int Y; public uint Private; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, nint window, uint filterMin, uint filterMax, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref NativeMessage message);

    private sealed class WorkerSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        private readonly AutoResetEvent _ready = new(false);
        private readonly object _lifecycle = new();
        private bool _disposed;
        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_lifecycle)
            {
                // Late vendor event callbacks cannot resurrect a disposed driver or run on a random thread.
                if (_disposed) return;
                _callbacks.Enqueue((d, state));
                _ready.Set();
            }
        }
        public void Drain() { while (_callbacks.TryDequeue(out var callback)) callback.Callback(callback.State); }
        public void Wait(int milliseconds) => _ready.WaitOne(milliseconds);
        public void Dispose()
        {
            lock (_lifecycle)
            {
                if (_disposed) return;
                _disposed = true;
                _callbacks.Clear();
                _ready.Dispose();
            }
        }
    }
}
