namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>The <see cref="OrchestrationProgress.Status"/> values.</summary>
internal enum OrchestrationRunStatus
{
  Running,
  Completed,
  Stopped,
  Failed,
}
