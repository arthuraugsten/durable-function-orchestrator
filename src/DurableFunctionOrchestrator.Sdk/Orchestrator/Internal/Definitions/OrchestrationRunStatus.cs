namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>The <see cref="OrchestrationProgress.Status"/> values.</summary>
internal enum OrchestrationRunStatus
{
  Running,
  Completed,
  Stopped,
  Failed,

  // Another run of the same orchestrator holds the run lease, so this run did nothing.
  Rejected,
}
