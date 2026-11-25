using Microsoft.DurableTask;

namespace DurableFunctionOrchestrator.Sdk;

/// <summary>Per-step configuration, for both activity and sub-orchestration steps.</summary>
public sealed class StepOptions<TInput>
{
  internal StepOptions() { }

  internal Func<OrchestrationState<TInput>, bool>? Condition { get; private set; }

  internal TaskOptions? TaskOptions { get; private set; }

  internal bool ShouldContinueOnError { get; private set; }

  /// <summary>Runs the step only when <paramref name="condition"/> is true; otherwise it is skipped.</summary>
  public StepOptions<TInput> When(Func<OrchestrationState<TInput>, bool> condition)
  {
    ArgumentNullException.ThrowIfNull(condition);
    Condition = condition;
    return this;
  }

  /// <summary>Retries the step using <paramref name="policy"/>, overriding the orchestrator default.</summary>
  public StepOptions<TInput> WithRetry(RetryPolicy policy)
  {
    ArgumentNullException.ThrowIfNull(policy);
    TaskOptions = TaskOptions.FromRetryPolicy(policy);
    return this;
  }

  /// <summary>
  /// Uses <paramref name="options"/> for this call, overriding the orchestrator default. For a
  /// sub-orchestration, pass a <see cref="SubOrchestrationOptions"/> to set its instance ID.
  /// </summary>
  public StepOptions<TInput> WithOptions(TaskOptions options)
  {
    ArgumentNullException.ThrowIfNull(options);
    TaskOptions = options;
    return this;
  }

  /// <summary>
  /// Records a failure of this step in <see cref="OrchestrationState{TInput}.Failures"/> and continues
  /// the pipeline instead of failing it. Use for non-critical steps such as notifications.
  /// </summary>
  public StepOptions<TInput> ContinueOnError()
  {
    ShouldContinueOnError = true;
    return this;
  }
}
