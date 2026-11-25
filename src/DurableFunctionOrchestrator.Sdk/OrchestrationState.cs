using System.Diagnostics.CodeAnalysis;
using Microsoft.DurableTask;

namespace DurableFunctionOrchestrator.Sdk;

/// <summary>
/// The state of a running pipeline: the orchestration input plus the outcome of every step so far,
/// keyed by <see cref="StepHandle"/>. Passed to input builders, conditions, <c>BuildOutput</c> and
/// <c>OnStepFailedAsync</c>.
/// </summary>
public sealed class OrchestrationState<TInput>
{
  private readonly Dictionary<StepHandle, object?> _results = [];
  private readonly List<StepHandle> _completedSteps = [];
  private readonly List<StepHandle> _skippedSteps = [];
  private readonly Dictionary<StepHandle, TaskFailureDetails> _failures = [];

  internal OrchestrationState(TInput input, string instanceId)
  {
    Input = input;
    InstanceId = instanceId;
  }

  /// <summary>The orchestration input.</summary>
  public TInput Input { get; }

  /// <summary>The orchestration instance ID.</summary>
  public string InstanceId { get; }

  /// <summary>Steps that completed successfully, in completion order.</summary>
  public IReadOnlyList<StepHandle> CompletedSteps => _completedSteps;

  /// <summary>Steps skipped because their condition was false.</summary>
  public IReadOnlyList<StepHandle> SkippedSteps => _skippedSteps;

  /// <summary>Failures of steps marked <c>ContinueOnError</c>.</summary>
  public IReadOnlyDictionary<StepHandle, TaskFailureDetails> Failures => _failures;

  /// <summary>True when a <c>StopIf</c> stage ended the pipeline early.</summary>
  public bool IsStopped => StopReason is not null;

  /// <summary>The reason given to the <c>StopIf</c> stage that ended the pipeline, if any.</summary>
  public string? StopReason { get; private set; }

  /// <summary>Gets the result of a completed step.</summary>
  /// <exception cref="InvalidOperationException">The step has not completed successfully.</exception>
  public TResult GetResult<TResult>(StepHandle<TResult> step)
  {
    ArgumentNullException.ThrowIfNull(step);

    if (!_results.TryGetValue(step, out object? value))
    {
      throw new InvalidOperationException(
        $"Step '{step.Name}' has no result. It has not run yet, was skipped or failed, or is not part of this pipeline. " +
        "Steps in the same parallel stage cannot read each other's results.");
    }

    return (TResult)value!;
  }

  /// <summary>Gets the result of a step if it completed successfully.</summary>
  public bool TryGetResult<TResult>(StepHandle<TResult> step, [MaybeNullWhen(false)] out TResult result)
  {
    ArgumentNullException.ThrowIfNull(step);

    if (_results.TryGetValue(step, out object? value))
    {
      result = (TResult)value!;
      return true;
    }

    result = default;
    return false;
  }

  internal void RecordSuccess(StepHandle step, object? result)
  {
    _results[step] = result;
    _completedSteps.Add(step);
  }

  internal void RecordSkipped(StepHandle step) => _skippedSteps.Add(step);

  internal void RecordFailure(StepHandle step, TaskFailureDetails details) => _failures[step] = details;

  internal void Stop(string reason) => StopReason = reason;
}
