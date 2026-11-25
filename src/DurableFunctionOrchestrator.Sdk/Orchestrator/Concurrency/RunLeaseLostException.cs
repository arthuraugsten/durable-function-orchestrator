namespace DurableFunctionOrchestrator.Sdk;

/// <summary>
/// Fails a run that no longer holds its orchestrator's lease while its pipeline is running: a renewal found
/// that another run took the lease, or every renewal failed until the lease could expire. The run stops
/// instead of continuing without the protection the lease gives.
/// </summary>
public sealed class RunLeaseLostException : Exception
{
  internal RunLeaseLostException(string orchestratorName, string instanceId, OrchestrationRunRecord? current)
    : base(current is null
      ? $"Instance '{instanceId}' lost the run lease of '{orchestratorName}': the record no longer exists."
      : $"Instance '{instanceId}' lost the run lease of '{orchestratorName}': the record now shows instance '{current.InstanceId}' as {current.Status}.")
  {
    Current = current;
  }

  internal RunLeaseLostException(string orchestratorName, string instanceId, Exception renewalFailure)
    : base($"Instance '{instanceId}' could not renew the run lease of '{orchestratorName}' before it could expire: {renewalFailure.Message}", renewalFailure)
  {
  }

  /// <summary>The record that replaced this run's lease, if the renewal could read one.</summary>
  public OrchestrationRunRecord? Current { get; }
}
