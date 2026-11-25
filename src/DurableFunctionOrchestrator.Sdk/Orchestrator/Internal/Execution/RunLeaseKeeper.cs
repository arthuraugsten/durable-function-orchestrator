using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>
/// Keeps a run's lease alive while the pipeline works. <see cref="CoverAsync{T}"/> races the work against a
/// durable timer: each time the timer fires first, it renews the lease and sets the next timer; when the
/// work finishes, the pending timer is cancelled. Every stage, however long (a polling step waiting for
/// hours, a long activity, a sub-orchestration), is covered the same way.
/// </summary>
/// <remarks>
/// All times come from <see cref="TaskOrchestrationContext.CurrentUtcDateTime"/> and every wait is a durable
/// timer, so the renewals replay deterministically.
/// </remarks>
internal sealed class RunLeaseKeeper
{
  private readonly TaskOrchestrationContext _context;
  private readonly RunLeaseOptions _lease;
  private readonly TaskOptions? _taskOptions;
  private readonly ILogger _logger;
  private DateTime _nextRenewalAt;
  private DateTime _expiresAt;

  public RunLeaseKeeper(TaskOrchestrationContext context, RunLeaseOptions lease, TaskOptions? taskOptions, ILogger logger)
  {
    _context = context;
    _lease = lease;
    _taskOptions = taskOptions;
    _logger = logger;

    // Measured from when the lease was acquired, on the orchestration clock.
    DateTime now = context.CurrentUtcDateTime;
    _nextRenewalAt = now.AddSeconds(lease.RenewalSeconds);
    _expiresAt = now.AddSeconds(lease.LeaseSeconds);
  }

  public async Task CoverAsync(Task work) =>
    await CoverAsync(AsResult(work));

  public async Task<T> CoverAsync<T>(Task<T> work)
  {
    while (!work.IsCompleted)
    {
      if (_context.CurrentUtcDateTime < _nextRenewalAt)
      {
        using var cancelTimer = new CancellationTokenSource();
        Task timer = _context.CreateTimer(_nextRenewalAt, cancelTimer.Token);

        if (await Task.WhenAny(work, timer) == work)
        {
          // An outstanding timer would keep the orchestration from completing.
          cancelTimer.Cancel();
          break;
        }
      }

      await RenewAsync();
    }

    return await work;
  }

  private async Task RenewAsync()
  {
    DateTime now = _context.CurrentUtcDateTime;
    string name = _context.Name.Name;
    var request = new RunLeaseRequest(name, _context.InstanceId, _lease.LeaseSeconds);

    try
    {
      RunLeaseResult result = await _context.CallActivityAsync<RunLeaseResult>(_lease.RenewActivityName, request, _taskOptions);
      if (!result.Acquired)
      {
        SdkLog.RunLeaseLost(_logger, name, _context.InstanceId, result.Record?.InstanceId);
        throw new RunLeaseLostException(name, _context.InstanceId, result.Record);
      }

      // Counted from before the call: the stored expiry is at least this late.
      _expiresAt = now.AddSeconds(_lease.LeaseSeconds);
      SdkLog.RunLeaseRenewed(_logger, name, _context.InstanceId);
    }
    catch (TaskFailedException ex)
    {
      // The lease is still valid until _expiresAt. Try again at the next renewal, unless that would be too late:
      // then fail now rather than keep running without the lease.
      SdkLog.RunLeaseRenewalFailed(_logger, name, _context.InstanceId, ex.FailureDetails.ErrorMessage);
      if (now.AddSeconds(_lease.RenewalSeconds) >= _expiresAt)
        throw new RunLeaseLostException(name, _context.InstanceId, ex);
    }

    _nextRenewalAt = now.AddSeconds(_lease.RenewalSeconds);
  }

  private static async Task<bool> AsResult(Task work)
  {
    await work;
    return true;
  }
}
