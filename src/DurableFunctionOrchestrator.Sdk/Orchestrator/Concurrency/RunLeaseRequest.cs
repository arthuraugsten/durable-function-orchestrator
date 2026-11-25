namespace DurableFunctionOrchestrator.Sdk;

/// <summary>The input of the acquire activity: which run wants the lease, and for how long.</summary>
/// <param name="OrchestratorName">The orchestrator's registered name: the record's primary key.</param>
/// <param name="InstanceId">The orchestration instance asking for the lease.</param>
/// <param name="LeaseSeconds">How long the lease lasts if the run never releases it.</param>
public sealed record RunLeaseRequest(string OrchestratorName, string InstanceId, int LeaseSeconds);
