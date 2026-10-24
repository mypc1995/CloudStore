using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CloudStore.Core;

public enum TransferKind
{
    Upload,
    Download,
}

public enum TransferState
{
    Waiting,
    Running,
    Completed,
    Failed,
    Canceled,
}

public sealed record TransferStats(int Running, int Waiting, int Completed, int Failed, int Canceled)
{
    public int Total => Running + Waiting + Completed + Failed + Canceled;

    public bool HasActive => Running + Waiting > 0;
}

/// <summary>一个上传或下载任务的运行时状态。</summary>
public sealed class TransferItem : INotifyPropertyChanged
{
    private long _transferred;
    private TransferState _state = TransferState.Waiting;
    private string? _error;
    private string? _result;
    private string _speedText = string.Empty;
    private int _lastPercent = -1;
    private long _speedMarkMs;
    private long _speedMarkBytes;
    private double _bytesPerSecond;
    private long _finishedTicks;

    public TransferItem(TransferKind kind, string title, string description, long size)
    {
        Kind = kind;
        Title = title;
        Description = description;
        Size = size;
        Id = Guid.NewGuid();
    }

    /// <summary>实际执行的逻辑，允许在构造之后赋值（便于闭包捕获 item 自身）。</summary>
    public Func<CancellationToken, Task<string?>>? Work { get; set; }

    public Guid Id { get; }

    public TransferKind Kind { get; }

    public string Title { get; }

    public string Description { get; set; }

    public long Size { get; set; }

    /// <summary>上传时的本地文件路径（用于重试）。</summary>
    public string? Source { get; init; }

    /// <summary>上传时的目标对象 key（用于重试）。</summary>
    public string? RemoteKey { get; init; }

    public CancellationTokenSource Cts { get; } = new();

    internal SynchronizationContext? Ui { get; set; }

    public long Transferred => Interlocked.Read(ref _transferred);

    public TransferState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            Raise();
            Raise(nameof(StatusText));
            Raise(nameof(Percent));
        }
    }

    public string? Error => _error;

    public string? Result => _result;

    public string SpeedText => _speedText;

    public int Percent => Size > 0
        ? (int)Math.Clamp(Transferred * 100 / Size, 0, 100)
        : (State == TransferState.Completed ? 100 : 0);

    public string StatusText => State switch
    {
        TransferState.Waiting => "等待中",
        TransferState.Running => Kind == TransferKind.Upload ? "上传中" : "下载中",
        TransferState.Completed => "已完成",
        TransferState.Failed => "失败",
        TransferState.Canceled => "已取消",
        _ => State.ToString(),
    };

    public string ProgressText => Size > 0
        ? string.Format("{0}%  {1} / {2}", Percent, Humanize.Bytes(Transferred), Humanize.Bytes(Size))
        : Humanize.Bytes(Transferred);

    public string EtaText => State == TransferState.Running && _bytesPerSecond > 1
        ? "剩余 " + Humanize.Eta(Size - Transferred, _bytesPerSecond)
        : string.Empty;

    public double BytesPerSecond => _bytesPerSecond;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Begin()
    {
        _speedMarkMs = Environment.TickCount64;
        _speedMarkBytes = Interlocked.Read(ref _transferred);
        State = TransferState.Running;
        Raise(nameof(Percent));
        Raise(nameof(StatusText));
    }

    internal void Report(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        long total = Interlocked.Add(ref _transferred, bytes);
        long now = Environment.TickCount64;
        int percent = Size > 0 ? (int)Math.Clamp(total * 100 / Size, 0, 100) : 0;

        if (percent == _lastPercent && now - _speedMarkMs < 250)
        {
            return;
        }

        _lastPercent = percent;
        UpdateSpeed(now);

        Raise(nameof(Transferred));
        Raise(nameof(Percent));
        Raise(nameof(ProgressText));
        Raise(nameof(SpeedText));
        Raise(nameof(EtaText));
    }

    private void UpdateSpeed(long now)
    {
        long elapsed = now - _speedMarkMs;
        if (elapsed < 200)
        {
            return;
        }

        long delta = Interlocked.Read(ref _transferred) - _speedMarkBytes;
        double speed = delta / (elapsed / 1000d);
        _speedMarkMs = now;
        _speedMarkBytes = Interlocked.Read(ref _transferred);

        _bytesPerSecond = speed;
        _speedText = Humanize.Speed(speed);
    }

    internal void MarkCompleted(string? result)
    {
        _result = result;
        _finishedTicks = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (Size > 0)
        {
            Interlocked.Exchange(ref _transferred, Size);
        }

        State = TransferState.Completed;
        _speedText = string.Empty;

        Raise(nameof(Transferred));
        Raise(nameof(ProgressText));
        Raise(nameof(SpeedText));
        Raise(nameof(EtaText));
        Raise(nameof(Percent));
    }

    internal void MarkFailed(string error)
    {
        _error = error;
        State = TransferState.Failed;
        _speedText = string.Empty;
        Raise(nameof(SpeedText));
        Raise(nameof(EtaText));
    }

    internal void MarkCanceled()
    {
        State = TransferState.Canceled;
        _speedText = string.Empty;
        Raise(nameof(SpeedText));
    }

    public void Cancel() => Cts.Cancel();

    internal Task<string?> RunWorkAsync() =>
        Work is null ? Task.FromResult<string?>(null) : Work(Cts.Token);

    private void Raise([CallerMemberName] string? name = null)
    {
        if (Ui is null)
        {
            OnPropertyChanged(name);
            return;
        }

        Ui.Post(_ => OnPropertyChanged(name), null);
    }

    private void OnPropertyChanged(string? name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
    }
}

/// <summary>全局传输队列：限制并发、串行调度、支持取消与清理。</summary>
public sealed class TransferManager : IDisposable
{
    private readonly ObservableCollection<TransferItem> _items = new();
    private readonly List<TransferItem> _all = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;
    private int _maxConcurrency = 3;
    private int _running;

    private TransferManager()
    {
    }

    public static TransferManager Instance { get; } = new();

    public ObservableCollection<TransferItem> Items => _items;

    /// <summary>任务状态变化事件（进度变化不走这里，避免频繁刷新）。</summary>
    public event EventHandler? Changed;

    /// <summary>单个任务终结事件，参数为已完成/失败的任务。</summary>
    public event EventHandler<TransferItem>? Finished;

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set
        {
            _maxConcurrency = Math.Clamp(value, 1, 16);
            _signal.Release();
        }
    }

    public int Count => _all.Count;

    public TransferStats Stats
    {
        get
        {
            lock (_sync)
            {
                return new TransferStats(
                    _running,
                    _all.Count(i => i.State == TransferState.Waiting),
                    _all.Count(i => i.State == TransferState.Completed),
                    _all.Count(i => i.State == TransferState.Failed),
                    _all.Count(i => i.State == TransferState.Canceled));
            }
        }
    }

    public void Add(TransferItem item)
    {
        item.Ui = SynchronizationContext.Current;

        lock (_sync)
        {
            _all.Add(item);
            _items.Add(item);
        }

        EnsureLoop();
        _signal.Release();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AddRange(IEnumerable<TransferItem> items)
    {
        foreach (TransferItem item in items.ToArray())
        {
            Add(item);
        }
    }

    public void Cancel(Guid id)
    {
        TransferItem? item = _all.FirstOrDefault(i => i.Id == id);
        if (item is null || item.State is TransferState.Completed or TransferState.Failed or TransferState.Canceled)
        {
            return;
        }

        item.Cancel();
    }

    public void CancelAll()
    {
        foreach (TransferItem item in _all.ToArray())
        {
            if (item.State is TransferState.Waiting or TransferState.Running)
            {
                item.Cancel();
            }
        }
    }

    public void ClearFinished()
    {
        lock (_sync)
        {
            var finished = _all
                .Where(i => i.State is TransferState.Completed or TransferState.Failed or TransferState.Canceled)
                .ToArray();

            foreach (TransferItem item in finished)
            {
                _all.Remove(item);
                _items.Remove(item);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureLoop()
    {
        _loop ??= Task.Run(() => SchedulerAsync(_shutdown.Token));
    }

    private async Task SchedulerAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TransferItem? next = null;

                lock (_sync)
                {
                    if (_running < _maxConcurrency)
                    {
                        next = _all.FirstOrDefault(i => i.State == TransferState.Waiting);
                        if (next is not null)
                        {
                            next.Begin();
                            _running++;
                        }
                    }
                }

                if (next is null)
                {
                    await _signal.WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                _ = Task.Run(() => RunAsync(next), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
    }

    private async Task RunAsync(TransferItem item)
    {
        try
        {
            string? result = await item.RunWorkAsync().ConfigureAwait(false);
            item.MarkCompleted(result);
        }
        catch (OperationCanceledException)
        {
            item.MarkCanceled();
        }
        catch (Exception ex)
        {
            item.MarkFailed(ErrorInfo.Describe(ex));
        }
        finally
        {
            lock (_sync)
            {
                _running--;
            }

            var ui = item.Ui;
            if (ui is not null)
            {
                ui.Post(_ => RaiseFinished(item), null);
            }
            else
            {
                RaiseFinished(item);
            }

            _signal.Release();
        }
    }

    private void RaiseFinished(TransferItem item)
    {
        Changed?.Invoke(this, EventArgs.Empty);
        Finished?.Invoke(this, item);
    }

    public void Dispose()
    {
        CancelAll();
        _shutdown.Cancel();
        _shutdown.Dispose();
        _signal.Dispose();
    }
}
