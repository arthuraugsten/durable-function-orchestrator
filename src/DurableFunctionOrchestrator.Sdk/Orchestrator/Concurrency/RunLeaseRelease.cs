namespace DurableFunctionOrchestrator.Sdk;

/// <summary>The input of the release activity: which run is ending, and how.</summary>
/// <param name="OrchestratorName">The orchestrator's registered name: the record's primary key.</param>
/// <param name="InstanceId">The orchestration instance that holds the lease.</param>
/// <param name="Status">How the run ended: <see cref="RunStatus.Completed"/>, <see cref="RunStatus.Stopped"/> or <see cref="RunStatus.Failed"/>.</param>
/// <param name="Error">Why the run failed, if it did.</param>
public sealed record RunLeaseRelease(string OrchestratorName, string InstanceId, RunStatus Status, string? Error = null);
