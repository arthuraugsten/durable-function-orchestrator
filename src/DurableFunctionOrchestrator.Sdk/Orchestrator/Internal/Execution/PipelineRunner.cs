using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>
/// Runs a built pipeline: each stage in order until the end or a <c>StopIf</c>, publishing progress
/// as the custom status, then routes a step failure to the orchestrator's failure handler.
/// </summary>
internal sealed class PipelineRunner<TInput>
{
  private readonly IReadOnlyList<IPipelineStage<TInput>> _stages;
  private readonly TaskOptions? _defaultTaskOptions;

  public PipelineRunner(IReadOnlyList<IPipelineStage<TInput>> stages, TaskOptions? defaultTaskOptions)
  {
    _stages = stages;
    _defaultTaskOptions = defaultTaskOptions;
  }

  public async Task<TOutput> RunAsync<TOutput>(
    TaskOrchestrationContext context,
    TInput input,
    ILogger logger,
    Func<OrchestrationState<TInput>, TOutput> buildOutput,
    Func<OrchestrationState<TInput>, StepFailedException, Task<TOutput>> onStepFailed)
  {
    var state = new OrchestrationState<TInput>(input, context.InstanceId);
    var runtime = new PipelineRuntime<TInput>(context, state, _defaultTaskOptions, logger);

    SdkLog.PipelineStarting(logger, context.InstanceId, _stages.Count);

    try
    {
      for (int i = 0; i < _stages.Count && !state.IsStopped; i++)
      {
        ReportProgress(context, state, OrchestrationRunStatus.Running, i + 1, _stages[i].Description);
        await _stages[i].ExecuteAsync(runtime);
      }
    }
    catch (StepFailedException failure)
    {
      SdkLog.PipelineFailed(logger, failure, context.InstanceId, failure.Step.Name);
      ReportProgress(context, state, OrchestrationRunStatus.Failed, null, failure.Step.Name);
      return await onStepFailed(state, failure);
    }

    OrchestrationRunStatus finalStatus = state.IsStopped ? OrchestrationRunStatus.Stopped : OrchestrationRunStatus.Completed;
    ReportProgress(context, state, finalStatus, null, state.StopReason);
    SdkLog.PipelineFinished(logger, context.InstanceId, finalStatus);

    return buildOutput(state);
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
