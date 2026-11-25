using Microsoft.DurableTask;

namespace DurableFunctionOrchestrator.Sdk;

/// <summary>Raised when a pipeline step fails after exhausting its retries.</summary>
public sealed class StepFailedException : Exception
{
  internal StepFailedException(StepHandle step, TaskFailedException innerException)
    : base($"Step '{step.Name}' ({step.Kind} '{step.TargetName}') failed: {innerException.FailureDetails.ErrorMessage}", innerException)
  {
    Step = step;
    FailureDetails = innerException.FailureDetails;
  }

  /// <summary>The step that failed. Compare it with your handles, e.g. <c>failure.Step == Payment</c>.</summary>
  public StepHandle Step { get; }

  /// <summary>Details of the failure (error type, message, stack trace).</summary>
  public TaskFailureDetails FailureDetails { get; }
}
