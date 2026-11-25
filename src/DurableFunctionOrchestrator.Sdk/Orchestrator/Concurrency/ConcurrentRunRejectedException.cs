namespace DurableFunctionOrchestrator.Sdk;

/// <summary>
/// Thrown, by default, when a run can't acquire its orchestrator's lease because another run holds it.
/// Override <c>ActivityOrchestrator.OnConcurrentRunRejectedAsync</c> to return an output instead of failing.
/// </summary>
public sealed class ConcurrentRunRejectedException : Exception
{
  internal ConcurrentRunRejectedException(OrchestrationRunRecord current)
    : base($"Orchestrator '{current.OrchestratorName}' is already running as instance '{current.InstanceId}' (lease until {current.LeaseExpiresAt:O}).")
  {
    Current = current;
  }

  /// <summary>The record of the run that holds the lease.</summary>
  public OrchestrationRunRecord Current { get; }
}
