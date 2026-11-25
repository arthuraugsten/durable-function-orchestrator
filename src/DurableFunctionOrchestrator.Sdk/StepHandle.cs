using Microsoft.DurableTask;

namespace DurableFunctionOrchestrator.Sdk;

/// <summary>
/// A typed reference to a pipeline step: what it calls, and the key its outcome is recorded under.
/// Create handles with <see cref="Steps"/>, usually as <c>static readonly</c> fields, and use the
/// same handle to add the step to the pipeline and to read its outcome from
/// <see cref="OrchestrationState{TInput}"/> or a <see cref="StepFailedException"/>.
/// </summary>
/// <remarks>
/// This non-generic handle is for steps with no result. Steps that return a value use
/// <see cref="StepHandle{TResult}"/>. Handles compare by value: same type, name, kind and target.
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

  /// <summary>Whether the step calls an activity or a sub-orchestration.</summary>
  public StepKind Kind { get; }

  /// <summary>The registered name of the activity or orchestrator the step calls.</summary>
  public string TargetName { get; }

  /// <inheritdoc />
  public sealed override string ToString() => Name;

  internal virtual async Task<object?> InvokeAsync(TaskOrchestrationContext context, object? input, TaskOptions? options)
  {
    if (Kind == StepKind.Activity)
      await context.CallActivityAsync(TargetName, input, options);
    else
      await context.CallSubOrchestratorAsync(TargetName, input, options);

    return null;
  }
}

/// <summary>A typed reference to a pipeline step that returns a <typeparamref name="TResult"/>.</summary>
/// <typeparam name="TResult">The type the activity or sub-orchestration returns.</typeparam>
public sealed record StepHandle<TResult> : StepHandle
{
  internal StepHandle(string name, StepKind kind, string targetName)
    : base(name, kind, targetName)
  {
  }

  internal override async Task<object?> InvokeAsync(TaskOrchestrationContext context, object? input, TaskOptions? options) =>
    Kind == StepKind.Activity
      ? await context.CallActivityAsync<TResult>(TargetName, input, options)
      : await context.CallSubOrchestratorAsync<TResult>(TargetName, input, options);
}
