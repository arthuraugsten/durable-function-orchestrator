namespace DurableFunctionOrchestrator.Sdk;

/// <summary>
/// The stored state of one orchestrator: which run holds it, or how its last run ended. There is one
/// record per orchestrator, keyed by <see cref="OrchestratorName"/>, so the record is what prevents two
/// instances of the same orchestrator from running at the same time, across every region.
/// </summary>
/// <param name="OrchestratorName">The primary key: the orchestrator's registered name.</param>
/// <param name="InstanceId">The orchestration instance that holds, or last held, the lease.</param>
/// <param name="Status">Whether a run holds the lease, or how the last run ended.</param>
/// <param name="StartedAt">When <paramref name="InstanceId"/> acquired the lease.</param>
/// <param name="LeaseExpiresAt">
/// When a <see cref="RunStatus.Running"/> record stops blocking other runs. A run that never releases
/// (for example because it was terminated) frees the orchestrator once this passes.
/// </param>
/// <param name="FinishedAt">When the run released the lease; null while it is running.</param>
/// <param name="Error">Why the run failed, when <paramref name="Status"/> is <see cref="RunStatus.Failed"/>.</param>
/// <param name="ETag">
/// The concurrency token, set by the repository on every write. Updates must pass the ETag they read
/// (see <see cref="IOrchestrationRunRepository.TryReplaceAsync"/>).
/// </param>
public sealed record OrchestrationRunRecord(
  string OrchestratorName,
  string InstanceId,
  RunStatus Status,
  DateTimeOffset StartedAt,
  DateTimeOffset LeaseExpiresAt,
  DateTimeOffset? FinishedAt = null,
  string? Error = null,
  string? ETag = null);
