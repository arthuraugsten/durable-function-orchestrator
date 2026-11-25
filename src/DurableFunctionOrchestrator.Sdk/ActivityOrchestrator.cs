using DurableFunctionOrchestrator.Sdk.Internal;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Sdk;

/// <summary>
/// Base class for orchestrators that run a pipeline of activities and sub-orchestrations.
/// Concrete classes declare <em>which</em> steps run (and in what shape) in
/// <see cref="ConfigurePipeline"/>; this class owns <em>how</em> they run: sequencing,
/// fan-out/fan-in, conditions, retries, progress reporting and failure handling.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline is rebuilt on every replay, so <see cref="ConfigurePipeline"/> must be
/// deterministic: no I/O, no <see cref="DateTime.UtcNow"/>, no random values.
/// </para>
/// <para>
/// With Azure Functions (isolated worker), expose the orchestrator with a one-line function:
/// <code>
/// [Function(nameof(MyOrchestrator))]
/// public Task&lt;TOutput&gt; Run([OrchestrationTrigger] TaskOrchestrationContext context) =&gt; ExecuteAsync(context);
/// </code>
/// Because this type derives from <see cref="TaskOrchestrator{TInput, TOutput}"/>, it can also be
/// registered as a class-based orchestrator.
/// </para>
/// </remarks>
/// <typeparam name="TInput">The orchestration input type.</typeparam>
/// <typeparam name="TOutput">The orchestration output type.</typeparam>
public abstract class ActivityOrchestrator<TInput, TOutput> : TaskOrchestrator<TInput, TOutput>
{
  /// <summary>
  /// Task options (for example a retry policy) applied to every step that does not set its own.
  /// </summary>
  protected virtual TaskOptions? DefaultTaskOptions => null;

  /// <summary>Declares the steps this orchestration runs.</summary>
  protected abstract void ConfigurePipeline(OrchestrationPipelineBuilder<TInput> pipeline);

  /// <summary>
  /// Builds the orchestration output once the pipeline has finished, or has been stopped by a
  /// <c>StopIf</c> stage (see <see cref="OrchestrationState{TInput}.IsStopped"/>).
  /// </summary>
  protected abstract TOutput BuildOutput(OrchestrationState<TInput> state);

  /// <summary>
  /// Called when a step fails after its retries are exhausted and it is not marked
  /// <see cref="StepOptions{TInput}.ContinueOnError"/>. Override to run compensating
  /// activities and/or return a failure output. The default rethrows, failing the orchestration.
  /// </summary>
  protected virtual Task<TOutput> OnStepFailedAsync(
    TaskOrchestrationContext context,
    OrchestrationState<TInput> state,
    StepFailedException failure) => Task.FromException<TOutput>(failure);

  /// <summary>Reads the orchestration input from <paramref name="context"/> and runs the pipeline.</summary>
  protected Task<TOutput> ExecuteAsync(TaskOrchestrationContext context)
  {
    ArgumentNullException.ThrowIfNull(context);
    return RunAsync(context, context.GetInput<TInput>()!);
  }

  /// <inheritdoc />
  public sealed override async Task<TOutput> RunAsync(TaskOrchestrationContext context, TInput input)
  {
    ArgumentNullException.ThrowIfNull(context);

    var builder = new OrchestrationPipelineBuilder<TInput>();
    ConfigurePipeline(builder);
    IReadOnlyList<IPipelineStage<TInput>> stages = builder.Build();

    ILogger logger = context.CreateReplaySafeLogger(GetType().Name);
    var state = new OrchestrationState<TInput>(input, context.InstanceId);
    var runtime = new PipelineRuntime<TInput>(context, state, DefaultTaskOptions, logger);

    logger.LogInformation("Orchestration {InstanceId} starting pipeline with {StageCount} stage(s).", context.InstanceId, stages.Count);

    try
    {
      for (int i = 0; i < stages.Count && !state.IsStopped; i++)
      {
        ReportProgress(context, state, OrchestrationRunStatus.Running, i + 1, stages.Count, stages[i].Description);
        await stages[i].ExecuteAsync(runtime);
      }
    }
    catch (StepFailedException failure)
    {
      logger.LogError(failure, "Orchestration {InstanceId} failed at step '{StepName}'.", context.InstanceId, failure.Step.Name);
      ReportProgress(context, state, OrchestrationRunStatus.Failed, null, stages.Count, failure.Step.Name);
      return await OnStepFailedAsync(context, state, failure);
    }

    OrchestrationRunStatus finalStatus = state.IsStopped ? OrchestrationRunStatus.Stopped : OrchestrationRunStatus.Completed;
    ReportProgress(context, state, finalStatus, null, stages.Count, state.StopReason);
    logger.LogInformation("Orchestration {InstanceId} finished with status {Status}.", context.InstanceId, finalStatus);

    return BuildOutput(state);
  }

  private static void ReportProgress(
    TaskOrchestrationContext context,
    OrchestrationState<TInput> state,
    OrchestrationRunStatus status,
    int? currentStage,
    int totalStages,
    string? detail) =>
    context.SetCustomStatus(new OrchestrationProgress(
      status.ToString(),
      currentStage,
      totalStages,
      detail,
      state.CompletedSteps.Select(step => step.Name).ToArray(),
      state.SkippedSteps.Select(step => step.Name).ToArray(),
      state.Failures.Keys.Select(step => step.Name).ToArray()));
}
