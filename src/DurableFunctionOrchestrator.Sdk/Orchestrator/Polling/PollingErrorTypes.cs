namespace DurableFunctionOrchestrator.Sdk;

/// <summary>
/// <see cref="Microsoft.DurableTask.TaskFailureDetails.ErrorType"/> values for failures raised by
/// polling steps themselves (rather than by their start or check activities). Use them in
/// <c>OnStepFailedAsync</c> to tell the cases apart.
/// </summary>
public static class PollingErrorTypes
{
  /// <summary>The status check reported <see cref="PollState.Failed"/>.</summary>
  public const string OperationFailed = "DurableFunctionOrchestrator.Sdk.PollingOperationFailed";

  /// <summary>The operation was still pending when the polling step's timeout elapsed.</summary>
  public const string TimedOut = "DurableFunctionOrchestrator.Sdk.PollingTimedOut";
}
