using Microsoft.DurableTask;

namespace DurableFunctionOrchestrator.Sdk.Tests.Fakes;

/// <summary>
/// Orchestrator whose pipeline and failure handling are supplied by the test. Its output is the
/// final <see cref="OrchestrationState{TInput}"/>, so tests can assert on results, skips and failures.
/// </summary>
internal sealed class TestOrchestrator<TInput> : ActivityOrchestrator<TInput, OrchestrationState<TInput>>
{
  private readonly Action<OrchestrationPipelineBuilder<TInput>> _configure;
  private readonly TaskOptions? _defaultTaskOptions;
  private readonly Func<TaskOrchestrationContext, OrchestrationState<TInput>, StepFailedException, Task<OrchestrationState<TInput>>>? _onStepFailed;
  private readonly RunLeaseOptions? _runLease;
  private readonly bool _returnStateWhenRejected;

  public TestOrchestrator(
    Action<OrchestrationPipelineBuilder<TInput>> configure,
    TaskOptions? defaultTaskOptions = null,
    Func<TaskOrchestrationContext, OrchestrationState<TInput>, StepFailedException, Task<OrchestrationState<TInput>>>? onStepFailed = null,
    RunLeaseOptions? runLease = null,
    bool returnStateWhenRejected = false)
  {
    _configure = configure;
    _defaultTaskOptions = defaultTaskOptions;
    _onStepFailed = onStepFailed;
    _runLease = runLease;
    _returnStateWhenRejected = returnStateWhenRejected;
  }

  public StepFailedException? HandledFailure { get; private set; }

  /// <summary>The lease holder's record, when the run was rejected.</summary>
  public OrchestrationRunRecord? RejectedBy { get; private set; }

  protected override TaskOptions? DefaultTaskOptions => _defaultTaskOptions;

  protected override RunLeaseOptions? RunLease => _runLease;

  protected override void ConfigurePipeline(OrchestrationPipelineBuilder<TInput> pipeline) => _configure(pipeline);

  protected override OrchestrationState<TInput> BuildOutput(OrchestrationState<TInput> state) => state;

  protected override Task<OrchestrationState<TInput>> OnStepFailedAsync(
    TaskOrchestrationContext context,
    OrchestrationState<TInput> state,
    StepFailedException failure)
  {
    HandledFailure = failure;
    return _onStepFailed is null
      ? base.OnStepFailedAsync(context, state, failure)
      : _onStepFailed(context, state, failure);
  }

  protected override Task<OrchestrationState<TInput>> OnConcurrentRunRejectedAsync(
    TaskOrchestrationContext context,
    OrchestrationState<TInput> state,
    OrchestrationRunRecord current)
  {
    RejectedBy = current;
    return _returnStateWhenRejected
      ? Task.FromResult(state)
      : base.OnConcurrentRunRejectedAsync(context, state, current);
  }

  /// <summary>Runs through the input-reading entry point used by function triggers.</summary>
  public Task<OrchestrationState<TInput>> ExecuteFromContextAsync(TaskOrchestrationContext context) => ExecuteAsync(context);
}
