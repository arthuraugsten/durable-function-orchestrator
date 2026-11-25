using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>
/// Calls the acquire and release activities of a run lease. The orchestrator name is the record's key,
/// and the instance ID identifies the run: both come from the context, so they are the same on every replay.
/// </summary>
internal static class RunLeaseGuard
{
  public static async Task<RunLeaseResult> AcquireAsync(TaskOrchestrationContext context, RunLeaseOptions lease, TaskOptions? options, ILogger logger)
  {
    var request = new RunLeaseRequest(context.Name.Name, context.InstanceId, lease.LeaseSeconds);
    RunLeaseResult result = await context.CallActivityAsync<RunLeaseResult>(lease.AcquireActivityName, request, options);

    if (result.Acquired)
      SdkLog.RunLeaseAcquired(logger, request.OrchestratorName, context.InstanceId, result.Record!.LeaseExpiresAt);
    else
      SdkLog.RunLeaseRejected(logger, request.OrchestratorName, context.InstanceId, result.Record!.InstanceId);

    return result;
  }

  /// <remarks>
  /// A failed release is logged, not raised: the run's own result matters more, and the lease
  /// expires on its own, so raising here would only hide the run's outcome.
  /// </remarks>
  public static async Task ReleaseAsync(
    TaskOrchestrationContext context,
    RunLeaseOptions lease,
    TaskOptions? options,
    RunStatus status,
    string? error,
    ILogger logger)
  {
    var release = new RunLeaseRelease(context.Name.Name, context.InstanceId, status, error);

    try
    {
      await context.CallActivityAsync(lease.ReleaseActivityName, release, options);
      SdkLog.RunLeaseReleased(logger, release.OrchestratorName, context.InstanceId, status);
    }
    catch (TaskFailedException ex)
    {
      SdkLog.RunLeaseReleaseFailed(logger, release.OrchestratorName, context.InstanceId, ex.FailureDetails.ErrorMessage);
    }
  }
}
