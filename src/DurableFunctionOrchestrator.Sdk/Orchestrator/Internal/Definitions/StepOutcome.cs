using Microsoft.DurableTask;

namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>How one step ended. Recorded into the state by <see cref="StepStage{TInput}"/>, in declaration order.</summary>
internal readonly record struct StepOutcome(StepOutcomeKind Kind, object? Result, TaskFailedException? Failure)
{
  public static StepOutcome Succeeded(object? result) => new(StepOutcomeKind.Succeeded, result, null);

  public static StepOutcome Skipped() => new(StepOutcomeKind.Skipped, null, null);

  public static StepOutcome Failed(TaskFailedException failure) => new(StepOutcomeKind.Failed, null, failure);
}
