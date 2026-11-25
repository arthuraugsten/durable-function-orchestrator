namespace DurableFunctionOrchestrator.Sdk;

/// <summary>
/// Creates <see cref="StepHandle"/>s. Declare them once, typically as <c>static readonly</c> fields
/// on the orchestrator:
/// <code>
/// private static readonly StepHandle&lt;PaymentReceipt&gt; Payment = Steps.Activity&lt;PaymentReceipt&gt;("ProcessPayment");
/// </code>
/// </summary>
/// <remarks>
/// The step name defaults to the activity or orchestrator name. Pass <c>name</c> to call the same
/// target more than once in a pipeline, because step names must be unique.
/// </remarks>
public static class Steps
{
  /// <summary>A step that calls an activity returning <typeparamref name="TResult"/>.</summary>
  public static StepHandle<TResult> Activity<TResult>(string activityName, string? name = null) =>
    new(name ?? activityName, StepKind.Activity, activityName);

  /// <summary>A step that calls an activity with no result.</summary>
  public static StepHandle Activity(string activityName, string? name = null) =>
    new(name ?? activityName, StepKind.Activity, activityName);

  /// <summary>A step that runs another orchestrator, returning <typeparamref name="TResult"/>, as a sub-orchestration.</summary>
  public static StepHandle<TResult> SubOrchestration<TResult>(string orchestratorName, string? name = null) =>
    new(name ?? orchestratorName, StepKind.SubOrchestration, orchestratorName);

  /// <summary>A step that runs another orchestrator with no result as a sub-orchestration.</summary>
  public static StepHandle SubOrchestration(string orchestratorName, string? name = null) =>
    new(name ?? orchestratorName, StepKind.SubOrchestration, orchestratorName);
}
