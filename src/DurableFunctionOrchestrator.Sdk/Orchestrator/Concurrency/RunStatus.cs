namespace DurableFunctionOrchestrator.Sdk;

/// <summary>The status of an orchestrator's current or most recent run, as stored in its <see cref="OrchestrationRunRecord"/>.</summary>
public enum RunStatus
{
  /// <summary>A run holds the lease. Other runs of the same orchestrator are rejected until it ends or the lease expires.</summary>
  Running,

  /// <summary>The last run finished its pipeline.</summary>
  Completed,

  /// <summary>The last run ended early at a <c>StopIf</c> stage.</summary>
  Stopped,

  /// <summary>The last run failed: a step failed (even if <c>OnStepFailedAsync</c> handled it), or the orchestrator threw.</summary>
  Failed,
}
