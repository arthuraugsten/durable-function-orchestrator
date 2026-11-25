using DurableFunctionOrchestrator.Sdk.Internal;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Sdk;

/// <summary>
/// A typed reference to a pipeline step: what it calls, and the key its outcome is recorded under.
/// Create handles with <see cref="Steps"/>, usually as <c>static readonly</c> fields, and use the
/// same handle to add the step to the pipeline and to read its outcome from
/// <see cref="OrchestrationState{TInput}"/> or a <see cref="StepFailedException"/>.
/// </summary>
/// <remarks>
/// This non-generic handle is for steps with no result. Steps that return a value use
/// <see cref="StepHandle{TResult}"/>. Handles compare by value: same type, name, kind and target
/// (and, for polling steps, the same start step, check step, interval and timeout).
/// </remarks>
public record StepHandle
{
  internal StepHandle(string name, StepKind kind, string targetName)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    ArgumentException.ThrowIfNullOrWhiteSpace(targetName);

    Name = name;
    Kind = kind;
    TargetName = targetName;
  }

  /// <summary>The step name: unique within a pipeline, and shown in logs and progress.</summary>
  public string Name { get; }

  /// <summary>Whether the step calls an activity, calls a sub-orchestration, or polls a long-running operation.</summary>
  public StepKind Kind { get; }

  /// <summary>
  /// The registered name of the activity or orchestrator the step calls. For a polling step, the
  /// target of its start step.
  /// </summary>
  public string TargetName { get; }

  /// <inheritdoc />
  public sealed override string ToString() => Name;

  // Type-dispatch hook: lets the runner call a step with its result type. The logic is in StepInvoker.
  internal virtual Task<object?> InvokeAsync(TaskOrchestrationContext context, object? input, TaskOptions? options, ILogger logger) =>
    StepInvoker.InvokeWithoutResultAsync(this, context, input, options);
}

/// <summary>A typed reference to a pipeline step that returns a <typeparamref name="TResult"/>.</summary>
/// <typeparam name="TResult">The type the activity, sub-orchestration or polled operation returns.</typeparam>
public sealed record StepHandle<TResult> : StepHandle
{
  internal StepHandle(string name, StepKind kind, string targetName)
    : base(name, kind, targetName) { }

  internal StepHandle(string name, PollingDefinition polling)
    : base(name, StepKind.Polling, polling.Start.TargetName) => Polling = polling;

  internal PollingDefinition? Polling { get; }

  internal override Task<object?> InvokeAsync(TaskOrchestrationContext context, object? input, TaskOptions? options, ILogger logger) =>
    StepInvoker.InvokeAsync(this, context, input, options, logger);
}
