using CS2HG.Core.Abstractions;
using CS2HG.Core.Localization;
using CS2HG.Core.Models;
using CS2HG.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace CS2HG.RenderingQueue;

public sealed record EnqueueResult(RenderJob Job, bool IsDuplicate, string? Reason);

/// <summary>
/// Persistent render queue. One job runs at a time (the game can only play one demo).
/// Jobs survive restarts: interrupted jobs resume from the last captured segment.
/// </summary>
public sealed class RenderQueue : IAsyncDisposable
{
    private readonly IRenderJobRepository _repository;
    private readonly IRenderPipeline _pipeline;
    private readonly ILogger<RenderQueue> _logger;
    private readonly List<RenderJob> _jobs = [];
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _currentCts;
    private string? _currentJobId;
    private StopReason _stopReason;
    private Task? _worker;
    private bool _paused;
    private DateTime _lastProgressSave = DateTime.MinValue;

    private enum StopReason
    {
        None,
        Pause,
        Cancel,
    }

    public RenderQueue(IRenderJobRepository repository, IRenderPipeline pipeline, ILogger<RenderQueue> logger)
    {
        _repository = repository;
        _pipeline = pipeline;
        _logger = logger;
    }

    public event Action<RenderJob>? JobUpdated;

    public event Action? QueueChanged;

    public bool IsPaused => _paused;

    public string? CurrentJobId => _currentJobId;

    public bool IsBusy => _currentJobId is not null;

    public IReadOnlyList<RenderJob> Jobs
    {
        get
        {
            lock (_gate) return _jobs.OrderBy(j => j.Order).ToList();
        }
    }

    /// <summary>Overall progress of the jobs that are not cancelled (0..1).</summary>
    public double TotalProgress
    {
        get
        {
            lock (_gate)
            {
                var active = _jobs.Where(j => j.Status != RenderJobStatus.Cancelled).ToList();
                return active.Count == 0 ? 0 : active.Average(j => j.Status == RenderJobStatus.Completed ? 1 : j.Progress);
            }
        }
    }

    public async Task InitializeAsync()
    {
        var stored = await _repository.GetAllAsync();
        lock (_gate)
        {
            _jobs.Clear();
            _jobs.AddRange(stored);
        }

        foreach (var job in stored.Where(j => j.Status == RenderJobStatus.Running))
        {
            // The app stopped mid-render: resume later from the captured segments.
            job.Status = RenderJobStatus.Pending;
            job.Stage = L.T("stage.interrupted");
            await _repository.SaveAsync(job);
        }

        QueueChanged?.Invoke();
    }

    public void Start()
    {
        _worker ??= Task.Run(() => WorkerAsync(_shutdown.Token));
        _signal.Release();
    }

    public async Task<EnqueueResult> EnqueueAsync(RenderJob job, bool force = false)
    {
        job.DedupeKey = Hashing.RenderDedupeKey(job);
        RenderJob? existing;
        lock (_gate)
        {
            existing = _jobs.FirstOrDefault(j => j.DedupeKey == job.DedupeKey &&
                                                  (j.Status is RenderJobStatus.Pending or RenderJobStatus.Running or RenderJobStatus.Paused ||
                                                   (j.Status == RenderJobStatus.Completed && j.OutputPath is not null && File.Exists(j.OutputPath))));
        }

        if (existing is not null && !force)
        {
            var reason = existing.Status == RenderJobStatus.Completed ? L.T("queue.dupRendered") : L.T("queue.dupQueued");
            return new EnqueueResult(existing, true, reason);
        }

        lock (_gate)
        {
            job.Order = _jobs.Count == 0 ? 1 : _jobs.Max(j => j.Order) + 1;
            job.Status = RenderJobStatus.Pending;
            _jobs.Add(job);
        }

        await _repository.SaveAsync(job);
        QueueChanged?.Invoke();
        _signal.Release();
        return new EnqueueResult(job, false, null);
    }

    public void Pause()
    {
        _paused = true;
        lock (_gate)
        {
            if (_currentCts is not null)
            {
                _stopReason = StopReason.Pause;
                _currentCts.Cancel();
            }
        }

        QueueChanged?.Invoke();
    }

    public async Task ResumeAsync()
    {
        _paused = false;
        foreach (var job in Jobs.Where(j => j.Status == RenderJobStatus.Paused))
        {
            job.Status = RenderJobStatus.Pending;
            await SaveAndNotifyAsync(job);
        }

        QueueChanged?.Invoke();
        _signal.Release();
    }

    public async Task CancelAsync(string jobId)
    {
        RenderJob? job;
        lock (_gate)
        {
            job = _jobs.FirstOrDefault(j => j.Id == jobId);
            if (job is null) return;
            if (_currentJobId == jobId && _currentCts is not null)
            {
                _stopReason = StopReason.Cancel;
                _currentCts.Cancel();
                return;
            }
        }

        if (job.IsFinished && job.Status != RenderJobStatus.Failed) return;
        job.Status = RenderJobStatus.Cancelled;
        job.Stage = L.T("stage.cancelled");
        await SaveAndNotifyAsync(job);
    }

    public async Task CancelAllAsync()
    {
        foreach (var job in Jobs.Where(j => !j.IsFinished)) await CancelAsync(job.Id);
    }

    public async Task RetryAsync(string jobId)
    {
        var job = Jobs.FirstOrDefault(j => j.Id == jobId);
        if (job is null || job.Status is RenderJobStatus.Running or RenderJobStatus.Pending) return;
        job.Status = RenderJobStatus.Pending;
        job.Error = null;
        job.Stage = L.T("stage.queued");
        job.Progress = 0;
        await SaveAndNotifyAsync(job);
        _signal.Release();
    }

    public async Task RemoveAsync(string jobId)
    {
        if (_currentJobId == jobId) return;
        lock (_gate) _jobs.RemoveAll(j => j.Id == jobId);
        await _repository.DeleteAsync(jobId);
        QueueChanged?.Invoke();
    }

    public async Task ClearFinishedAsync()
    {
        foreach (var job in Jobs.Where(j => j.IsFinished)) await RemoveAsync(job.Id);
    }

    public async Task MoveAsync(string jobId, int direction)
    {
        RenderJob? a, b;
        lock (_gate)
        {
            var ordered = _jobs.OrderBy(j => j.Order).ToList();
            var i = ordered.FindIndex(j => j.Id == jobId);
            var k = i + Math.Sign(direction);
            if (i < 0 || k < 0 || k >= ordered.Count) return;
            a = ordered[i];
            b = ordered[k];
            (a.Order, b.Order) = (b.Order, a.Order);
        }

        await _repository.SaveAsync(a);
        await _repository.SaveAsync(b);
        QueueChanged?.Invoke();
    }

    private RenderJob? NextPending()
    {
        lock (_gate) return _jobs.Where(j => j.Status == RenderJobStatus.Pending).OrderBy(j => j.Order).FirstOrDefault();
    }

    private async Task WorkerAsync(CancellationToken shutdown)
    {
        var hadWork = false;
        while (!shutdown.IsCancellationRequested)
        {
            var job = _paused ? null : NextPending();
            if (job is null)
            {
                if (hadWork)
                {
                    hadWork = false;
                    try
                    {
                        await _pipeline.OnQueueIdleAsync(shutdown);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Queue idle hook failed");
                    }
                }

                try
                {
                    await _signal.WaitAsync(TimeSpan.FromSeconds(2), shutdown);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            hadWork = true;
            await RunJobAsync(job, shutdown);
        }
    }

    private async Task RunJobAsync(RenderJob job, CancellationToken shutdown)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        lock (_gate)
        {
            _currentCts = cts;
            _currentJobId = job.Id;
            _stopReason = StopReason.None;
        }

        job.Status = RenderJobStatus.Running;
        job.StartedAt ??= DateTime.UtcNow;
        job.Attempts++;
        job.Error = null;
        await SaveAndNotifyAsync(job);
        QueueChanged?.Invoke();

        var progress = new Progress<RenderProgress>(p =>
        {
            job.Progress = p.Fraction;
            job.Stage = p.Stage;
            JobUpdated?.Invoke(job);
            if (DateTime.UtcNow - _lastProgressSave > TimeSpan.FromSeconds(3))
            {
                _lastProgressSave = DateTime.UtcNow;
                _ = _repository.SaveAsync(job);
            }
        });

        try
        {
            _logger.LogInformation("Rendering job {Id}: {Title} (round {Round}, attempt {Attempt})", job.Id, job.Title, job.Round, job.Attempts);
            await _pipeline.RenderAsync(job, j => _repository.SaveAsync(j), progress, cts.Token);
            job.Status = RenderJobStatus.Completed;
            job.Progress = 1;
            job.Stage = L.T("stage.done");
            job.FinishedAt = DateTime.UtcNow;
        }
        catch (OperationCanceledException) when (_stopReason == StopReason.Pause)
        {
            job.Status = RenderJobStatus.Paused;
            job.Stage = L.F("stage.paused", job.Captured.Count, Math.Max(job.Segments.Count, 1));
        }
        catch (OperationCanceledException) when (_stopReason == StopReason.Cancel)
        {
            job.Status = RenderJobStatus.Cancelled;
            job.Stage = L.T("stage.cancelled");
            job.FinishedAt = DateTime.UtcNow;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            job.Status = RenderJobStatus.Pending;
            job.Stage = L.T("stage.interrupted");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Render job {Id} failed", job.Id);
            job.Status = RenderJobStatus.Failed;
            job.Error = ex.Message;
            job.Stage = L.T("stage.failed");
            job.FinishedAt = DateTime.UtcNow;
        }
        finally
        {
            lock (_gate)
            {
                _currentCts = null;
                _currentJobId = null;
            }
        }

        await SaveAndNotifyAsync(job);
        QueueChanged?.Invoke();
    }

    private async Task SaveAndNotifyAsync(RenderJob job)
    {
        await _repository.SaveAsync(job);
        JobUpdated?.Invoke(job);
    }

    /// <summary>Waits until no job is pending or running (used by the CLI and tests).</summary>
    public async Task WaitForIdleAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Both checks under one lock: the worker can take a pending job between two separate reads.
            lock (_gate)
            {
                if (_currentJobId is null && _jobs.All(j => j.Status != RenderJobStatus.Pending)) return;
            }

            await Task.Delay(250, ct);
        }
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _shutdown.CancelAsync();
        if (_worker is not null)
        {
            try { await _worker; } catch { /* ignore */ }
        }

        _shutdown.Dispose();
        _signal.Dispose();
    }
}
