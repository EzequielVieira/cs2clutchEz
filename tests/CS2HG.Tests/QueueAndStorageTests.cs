using CS2HG.Core.Abstractions;
using CS2HG.Core.Models;
using CS2HG.Core.Settings;
using CS2HG.RenderingQueue;
using CS2HG.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace CS2HG.Tests;

/// <summary>Pipeline double: simulates capture of one segment per call step, with controllable failures.</summary>
public sealed class FakePipeline : IRenderPipeline
{
    public int Calls;
    public int IdleCalls;
    public Func<RenderJob, int, Exception?> FailOn = (_, _) => null;
    public TimeSpan SegmentTime = TimeSpan.FromMilliseconds(30);

    public async Task RenderAsync(RenderJob job, Func<RenderJob, Task> saveState, IProgress<RenderProgress> progress, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        foreach (var seg in job.Segments.Where(s => job.Captured.All(c => c.Index != s.Index)))
        {
            await Task.Delay(SegmentTime, ct);
            if (FailOn(job, seg.Index) is { } ex) throw ex;
            job.Captured.Add(new CapturedSegment { Index = seg.Index, DurationSeconds = 1 });
            await saveState(job);
            progress.Report(new RenderProgress(job.Id, (double)job.Captured.Count / job.Segments.Count, "capturando"));
        }

        job.OutputPath = Path.Combine(Path.GetTempPath(), job.Id + ".mp4");
    }

    public Task OnQueueIdleAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref IdleCalls);
        return Task.CompletedTask;
    }
}

public class QueueAndStorageTests
{
    private static RenderJob Job(string highlight, int segments = 2) => new()
    {
        HighlightId = highlight,
        Title = highlight,
        Export = new ExportSettings(),
        Segments = Enumerable.Range(0, segments).Select(i => new ClipSegment { Index = i, StartTick = i * 1000, EndTick = i * 1000 + 640 }).ToList(),
    };

    private static async Task<(RenderQueue Queue, RenderJobRepository Repo, TempDir Dir)> Create(FakePipeline pipeline, TempDir? dir = null)
    {
        dir ??= new TempDir();
        var repo = new RenderJobRepository(new AppDatabase(dir.Path));
        var queue = new RenderQueue(repo, pipeline, NullLogger<RenderQueue>.Instance);
        await queue.InitializeAsync();
        return (queue, repo, dir);
    }

    [Fact]
    public async Task Jobs_run_in_order_and_complete()
    {
        var pipeline = new FakePipeline();
        var (queue, repo, dir) = await Create(pipeline);
        using var _ = dir;
        await using var __ = queue;
        await queue.EnqueueAsync(Job("a"));
        await queue.EnqueueAsync(Job("b"));
        queue.Start();
        await queue.WaitForIdleAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        Assert.All(queue.Jobs, j => Assert.Equal(RenderJobStatus.Completed, j.Status));
        Assert.Equal(1, queue.TotalProgress, 3);
        var stored = await repo.GetAllAsync();
        Assert.All(stored, j => Assert.Equal(RenderJobStatus.Completed, j.Status));
        await Task.Delay(100);
        Assert.True(pipeline.IdleCalls >= 1);
    }

    [Fact]
    public async Task Duplicate_renders_are_not_enqueued()
    {
        var (queue, _, dir) = await Create(new FakePipeline());
        using var _d = dir;
        await using var _q = queue;
        var first = await queue.EnqueueAsync(Job("same"));
        var second = await queue.EnqueueAsync(Job("same"));
        Assert.False(first.IsDuplicate);
        Assert.True(second.IsDuplicate);
        Assert.Single(queue.Jobs);

        var different = Job("same");
        different.Export.Fps = 30;
        Assert.False((await queue.EnqueueAsync(different)).IsDuplicate);
    }

    [Fact]
    public async Task Failure_marks_job_failed_and_retry_resumes_from_saved_segments()
    {
        var pipeline = new FakePipeline();
        var failOnce = true;
        pipeline.FailOn = (_, index) => index == 1 && failOnce ? new InvalidOperationException("CS2 fechou") : null;
        var (queue, _, dir) = await Create(pipeline);
        using var _d = dir;
        await using var _q = queue;
        var job = (await queue.EnqueueAsync(Job("x", 3))).Job;
        queue.Start();
        await queue.WaitForIdleAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        Assert.Equal(RenderJobStatus.Failed, job.Status);
        Assert.Equal("CS2 fechou", job.Error);
        Assert.Single(job.Captured); // segment 0 survived

        failOnce = false;
        await queue.RetryAsync(job.Id);
        await queue.WaitForIdleAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.Equal(RenderJobStatus.Completed, job.Status);
        Assert.Equal([0, 1, 2], job.Captured.Select(c => c.Index).Order());
        Assert.Equal(2, job.Attempts);
    }

    [Fact]
    public async Task Pause_keeps_partial_results_and_resume_continues()
    {
        var pipeline = new FakePipeline { SegmentTime = TimeSpan.FromMilliseconds(250) };
        var (queue, _, dir) = await Create(pipeline);
        using var _d = dir;
        await using var _q = queue;
        var job = (await queue.EnqueueAsync(Job("p", 4))).Job;
        queue.Start();
        while (job.Captured.Count < 1) await Task.Delay(20);
        queue.Pause();
        while (job.Status == RenderJobStatus.Running) await Task.Delay(20);

        Assert.Equal(RenderJobStatus.Paused, job.Status);
        var saved = job.Captured.Count;
        Assert.InRange(saved, 1, 3);

        await queue.ResumeAsync();
        await queue.WaitForIdleAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.Equal(RenderJobStatus.Completed, job.Status);
        Assert.Equal(4, job.Captured.Count);
    }

    [Fact]
    public async Task Cancel_running_job()
    {
        var pipeline = new FakePipeline { SegmentTime = TimeSpan.FromMilliseconds(300) };
        var (queue, _, dir) = await Create(pipeline);
        using var _d = dir;
        await using var _q = queue;
        var job = (await queue.EnqueueAsync(Job("c", 5))).Job;
        queue.Start();
        while (job.Status != RenderJobStatus.Running) await Task.Delay(10);
        await queue.CancelAsync(job.Id);
        while (job.Status == RenderJobStatus.Running) await Task.Delay(10);
        Assert.Equal(RenderJobStatus.Cancelled, job.Status);
    }

    [Fact]
    public async Task Interrupted_jobs_are_recovered_after_restart()
    {
        var dir = new TempDir();
        using var _d = dir;
        var repo = new RenderJobRepository(new AppDatabase(dir.Path));
        var job = Job("crash", 3);
        job.Status = RenderJobStatus.Running;
        job.Captured.Add(new CapturedSegment { Index = 0, DurationSeconds = 1 });
        await repo.SaveAsync(job);

        var pipeline = new FakePipeline();
        var (queue, _, _) = await Create(pipeline, dir);
        await using var _q = queue;
        var restored = Assert.Single(queue.Jobs);
        Assert.Equal(RenderJobStatus.Pending, restored.Status);
        Assert.Single(restored.Captured);

        queue.Start();
        await queue.WaitForIdleAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.Equal(RenderJobStatus.Completed, restored.Status);
        Assert.Equal(3, restored.Captured.Count);
    }

    [Fact]
    public async Task Highlights_and_demos_round_trip_through_sqlite()
    {
        using var dir = new TempDir();
        var db = new AppDatabase(dir.Path);
        var demos = new DemoRepository(db);
        demos.Upsert(new DemoRecord { Hash = "h", FileName = "a.dem", Map = "de_test", Rounds = 24, ImportedAt = DateTime.UtcNow.ToString("O") });
        Assert.Equal("de_test", demos.Get("h")!.Map);

        var repo = new HighlightRepository(db);
        var h = new Highlight
        {
            Id = "x1", DemoHash = "h", Round = 3, PlayerSteamId = 76561198000000001, Title = "Ace", Score = 150,
            Components = [new HighlightComponent { Kind = HighlightKind.Ace, Score = 100, Tick = 5 }],
            ActionTicks = [1, 2, 3],
        };
        repo.Replace("h", h.PlayerSteamId, [h]);
        var loaded = Assert.Single(repo.Get("h", h.PlayerSteamId));
        Assert.Equal(150, loaded.Score);
        Assert.Equal(HighlightKind.Ace, loaded.Components[0].Kind);
        Assert.Equal([1, 2, 3], loaded.ActionTicks);
    }
}
