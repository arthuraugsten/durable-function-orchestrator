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

    logger.LogInformation("Orchestration {InstanceId} starting pipeline with {StageCount} stage(s).", context.InstanceId, _stages.Count);

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
      logger.LogError(failure, "Orchestration {InstanceId} failed at step '{StepName}'.", context.InstanceId, failure.Step.Name);
      ReportProgress(context, state, OrchestrationRunStatus.Failed, null, failure.Step.Name);
      return await onStepFailed(state, failure);
    }

    OrchestrationRunStatus finalStatus = state.IsStopped ? OrchestrationRunStatus.Stopped : OrchestrationRunStatus.Completed;
    ReportProgress(context, state, finalStatus, null, state.StopReason);
    logger.LogInformation("Orchestration {InstanceId} finished with status {Status}.", context.InstanceId, finalStatus);

    return buildOutput(state);
  }

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
      state.CompletedSteps.Select(step => step.Name).ToArray(),
      state.SkippedSteps.Select(step => step.Name).ToArray(),
      state.Failures.Keys.Select(step => step.Name).ToArray()));
}
