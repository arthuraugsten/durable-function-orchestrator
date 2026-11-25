using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>
/// Runs a built pipeline: each stage in order until the end or a <c>StopIf</c>, publishing progress
/// as the custom status, then routes a step failure to the orchestrator's failure handler. With a run
/// lease, it first acquires the lease (or rejects the run) and always releases it when the run ends.
/// </summary>
internal sealed class PipelineRunner<TInput>
{
  private readonly IReadOnlyList<IPipelineStage<TInput>> _stages;
  private readonly TaskOptions? _defaultTaskOptions;
  private readonly RunLeaseOptions? _runLease;

  public PipelineRunner(IReadOnlyList<IPipelineStage<TInput>> stages, TaskOptions? defaultTaskOptions, RunLeaseOptions? runLease)
  {
    _stages = stages;
    _defaultTaskOptions = defaultTaskOptions;
    _runLease = runLease;
  }

  public async Task<TOutput> RunAsync<TOutput>(
    TaskOrchestrationContext context,
    TInput input,
    ILogger logger,
    Func<OrchestrationState<TInput>, TOutput> buildOutput,
    Func<OrchestrationState<TInput>, StepFailedException, Task<TOutput>> onStepFailed,
    Func<OrchestrationState<TInput>, OrchestrationRunRecord, Task<TOutput>> onRunRejected)
  {
    var state = new OrchestrationState<TInput>(input, context.InstanceId);
    var runtime = new PipelineRuntime<TInput>(context, state, _defaultTaskOptions, logger);
    TaskOptions? leaseOptions = _runLease?.TaskOptions ?? _defaultTaskOptions;

    SdkLog.PipelineStarting(logger, context.InstanceId, _stages.Count);

    RunLeaseKeeper? keeper = null;
    if (_runLease is not null)
    {
      RunLeaseResult lease = await RunLeaseGuard.AcquireAsync(context, _runLease, leaseOptions, logger);
      if (!lease.Acquired)
      {
        ReportProgress(context, state, OrchestrationRunStatus.Rejected, null, $"Already running as instance {lease.Record!.InstanceId}.");
        return await onRunRejected(state, lease.Record);
      }

      keeper = new RunLeaseKeeper(context, _runLease, leaseOptions, logger);
    }

    // How the run ended, for the lease record. Anything that escapes without setting it is a failure.
    RunStatus outcome = RunStatus.Failed;
    string? error = "The orchestrator threw an unexpected exception.";

    try
    {
      try
      {
        for (int i = 0; i < _stages.Count && !state.IsStopped; i++)
        {
          ReportProgress(context, state, OrchestrationRunStatus.Running, i + 1, _stages[i].Description);

          // With a run lease, the lease is renewed for as long as the stage runs.
          Task stage = _stages[i].ExecuteAsync(runtime);
          await (keeper is null ? stage : keeper.CoverAsync(stage));
        }
      }
      catch (StepFailedException failure)
      {
        error = failure.Message;
        SdkLog.PipelineFailed(logger, failure, context.InstanceId, failure.Step.Name);
        ReportProgress(context, state, OrchestrationRunStatus.Failed, null, failure.Step.Name);

        // Compensation can take time too, so it keeps the lease.
        Task<TOutput> handled = onStepFailed(state, failure);
        return await (keeper is null ? handled : keeper.CoverAsync(handled));
      }
      catch (RunLeaseLostException lost)
      {
        error = lost.Message;
        throw;
      }

      OrchestrationRunStatus finalStatus = state.IsStopped ? OrchestrationRunStatus.Stopped : OrchestrationRunStatus.Completed;
      ReportProgress(context, state, finalStatus, null, state.StopReason);
      SdkLog.PipelineFinished(logger, context.InstanceId, finalStatus);

      TOutput output = buildOutput(state);
      (outcome, error) = (state.IsStopped ? RunStatus.Stopped : RunStatus.Completed, null);
      return output;
    }
    finally
    {
      // Released however the run ends (completed, stopped, failed, compensated or rethrown), so the
      // next run never has to wait for the lease to expire.
      if (_runLease is not null)
        await RunLeaseGuard.ReleaseAsync(context, _runLease, leaseOptions, outcome, error, logger);
    }
  }

  // Runs before every stage on every replay, so the name arrays are filled by plain loops
  // into exactly-sized arrays (no LINQ iterators or resizing).
  private void ReportProgress(
    TaskOrchestrationContext context,
    OrchestrationState<TInput> state,
    OrchestrationRunStatus status,
    int? currentStage,
    string? detail) =>
    context.SetCustomStatus(new OrchestrationProgress(
      status.ToString(),
      currentStage,
      _stages.Count,
      detail,
      Names(state.CompletedSteps),
      Names(state.SkippedSteps),
      Names(state.Failures.Keys, state.Failures.Count)));

  private static string[] Names(IReadOnlyList<StepHandle> steps)
  {
    if (steps.Count == 0)
      return [];

    var names = new string[steps.Count];
    for (int i = 0; i < names.Length; i++)
      names[i] = steps[i].Name;

    return names;
  }

  private static string[] Names(IEnumerable<StepHandle> steps, int count)
  {
    if (count == 0)
      return [];

    var names = new string[count];
    int i = 0;
    foreach (StepHandle step in steps)
      names[i++] = step.Name;

    return names;
  }
}
