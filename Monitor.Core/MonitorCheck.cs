using System.Diagnostics;

namespace Monitor.Core;

/// <summary>
/// One periodic check. Subclass this for each thing being monitored;
/// the runner handles scheduling, timing and error trapping.
/// </summary>
public abstract class MonitorCheck
{
    public abstract string Name { get; }

    /// <summary>Groups related checks in the UI.</summary>
    public virtual string Category => "General";

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    public bool IsEnabled { get; set; } = true;

    public DateTime? LastRun { get; private set; }

    public CheckResult? LastResult { get; private set; }

    public bool IsDue(DateTime now)
        => IsEnabled && (LastRun == null || now - LastRun.Value >= Interval);

    /// <summary>The actual work. Throwing here is fine - the runner catches it.</summary>
    protected abstract Task<CheckResult> ExecuteAsync(CancellationToken ct);

    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public async Task<CheckResult> RunAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return new CheckResult { Success = true, Message = "Still running from an earlier start, skipped.", RanAt = DateTime.Now };

        try
        {
            return await RunOnceAsync(ct);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<CheckResult> RunOnceAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        CheckResult result;

        try
        {
            result = await ExecuteAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new CheckResult
            {
                Success = false,
                Message = ex.Message,
                Details = ex.ToString()
            };
        }

        stopwatch.Stop();

        result.Duration = stopwatch.Elapsed;
        result.RanAt = DateTime.Now;

        LastRun = result.RanAt;
        LastResult = result;

        return result;
    }

    protected static CheckResult Ok(string message, string? details = null)
        => new() { Success = true, Message = message, Details = details };

    protected static CheckResult Attention(string message, string? details = null)
        => new() { Success = true, NeedsAttention = true, Message = message, Details = details };

    protected static CheckResult Failed(string message, string? details = null)
        => new() { Success = false, Message = message, Details = details };
}
