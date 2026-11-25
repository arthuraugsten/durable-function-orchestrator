namespace DurableFunctionOrchestrator.Sdk;

/// <summary>
/// Acquires and releases the run lease of an orchestrator in an <see cref="IOrchestrationRunRepository"/>.
/// Call it from the acquire and release activities (see <see cref="RunLeaseOptions"/>); it does I/O, so it
/// must never run in orchestration code.
/// </summary>
/// <remarks>
/// <para>
/// Every write is conditional (insert-if-absent, or replace-if-ETag-matches), so when several instances
/// race for the same orchestrator, exactly one wins; the others re-read and see the winner's record.
/// </para>
/// <para>
/// Both operations are idempotent, because activities can run more than once: acquiring again with the
/// instance that holds the lease succeeds without a write, and releasing a lease that the instance no
/// longer holds does nothing.
/// </para>
/// </remarks>
public sealed class RunLeaseService
{
  /// <summary>How many times an operation re-reads and retries after losing a race, before it gives up.</summary>
  public const int MaxConflictRetries = 5;

  private readonly IOrchestrationRunRepository _repository;
  private readonly TimeProvider _timeProvider;

  /// <summary>Creates the service over <paramref name="repository"/>, reading the clock from <paramref name="timeProvider"/>.</summary>
  public RunLeaseService(IOrchestrationRunRepository repository, TimeProvider? timeProvider = null)
  {
    ArgumentNullException.ThrowIfNull(repository);

    _repository = repository;
    _timeProvider = timeProvider ?? TimeProvider.System;
  }

  /// <summary>
  /// Gives <see cref="RunLeaseRequest.InstanceId"/> the lease unless another run of the same orchestrator
  /// holds an unexpired one.
  /// </summary>
  /// <exception cref="InvalidOperationException">Every attempt lost a race; the activity's retry policy can try again.</exception>
  public async Task<RunLeaseResult> TryAcquireAsync(RunLeaseRequest request, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.OrchestratorName);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceId);
    ArgumentOutOfRangeException.ThrowIfLessThan(request.LeaseSeconds, RunLeaseOptions.MinLeaseSeconds);

    for (int attempt = 0; attempt <= MaxConflictRetries; attempt++)
    {
      OrchestrationRunRecord? current = await _repository.GetAsync(request.OrchestratorName, cancellationToken);
      DateTimeOffset now = _timeProvider.GetUtcNow();

      // Already ours: the acquire activity ran again (a retry, or a lost response).
      if (current is { Status: RunStatus.Running } && current.InstanceId == request.InstanceId)
        return new RunLeaseResult(Acquired: true, current);

      // Held by another run whose lease hasn't expired.
      if (current is { Status: RunStatus.Running } && current.LeaseExpiresAt > now)
        return new RunLeaseResult(Acquired: false, current);

      // Free: never run, last run ended, or the holder's lease expired.
      var mine = new OrchestrationRunRecord(
        request.OrchestratorName,
        request.InstanceId,
        RunStatus.Running,
        StartedAt: now,
        LeaseExpiresAt: now.AddSeconds(request.LeaseSeconds));

      bool written = current is null
        ? await _repository.TryInsertAsync(mine, cancellationToken)
        : await _repository.TryReplaceAsync(mine, current.ETag!, cancellationToken);

      if (written)
        return new RunLeaseResult(Acquired: true, mine);
    }

    throw new InvalidOperationException(
      $"Could not acquire the run lease for '{request.OrchestratorName}': lost {MaxConflictRetries + 1} write races in a row.");
  }

  /// <summary>
  /// Extends the lease of <see cref="RunLeaseRequest.InstanceId"/> to <see cref="RunLeaseRequest.LeaseSeconds"/>
  /// from now, if it still holds it. Returns <see cref="RunLeaseResult.Acquired"/> false, without writing, when it
  /// doesn't: another run took the lease after it expired, or the record is gone. The renewing run must then stop.
  /// </summary>
  /// <remarks>
  /// A renewal never takes a lease it doesn't hold, even a free one: a run that lost its lease may already
  /// have overlapped with another run, so it fails instead of carrying on as if nothing happened.
  /// </remarks>
  /// <exception cref="InvalidOperationException">Every attempt lost a race; the activity's retry policy can try again.</exception>
  public async Task<RunLeaseResult> RenewAsync(RunLeaseRequest request, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.OrchestratorName);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceId);
    ArgumentOutOfRangeException.ThrowIfLessThan(request.LeaseSeconds, RunLeaseOptions.MinLeaseSeconds);

    for (int attempt = 0; attempt <= MaxConflictRetries; attempt++)
    {
      OrchestrationRunRecord? current = await _repository.GetAsync(request.OrchestratorName, cancellationToken);
      if (current is not { Status: RunStatus.Running } || current.InstanceId != request.InstanceId)
        return new RunLeaseResult(Acquired: false, current);

      OrchestrationRunRecord renewed = current with { LeaseExpiresAt = _timeProvider.GetUtcNow().AddSeconds(request.LeaseSeconds) };
      if (await _repository.TryReplaceAsync(renewed, current.ETag!, cancellationToken))
        return new RunLeaseResult(Acquired: true, renewed);
    }

    throw new InvalidOperationException(
      $"Could not renew the run lease for '{request.OrchestratorName}': lost {MaxConflictRetries + 1} write races in a row.");
  }

  /// <summary>
  /// Records how the run ended and frees the orchestrator, if <see cref="RunLeaseRelease.InstanceId"/>
  /// still holds the lease. Returns false when it no longer does (it already released, or its lease
  /// expired and another run took over), in which case nothing is written.
  /// </summary>
  /// <exception cref="InvalidOperationException">Every attempt lost a race; the activity's retry policy can try again.</exception>
  public async Task<bool> ReleaseAsync(RunLeaseRelease release, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(release);
    ArgumentException.ThrowIfNullOrWhiteSpace(release.OrchestratorName);
    ArgumentException.ThrowIfNullOrWhiteSpace(release.InstanceId);

    if (release.Status == RunStatus.Running)
      throw new ArgumentException("A released run must have ended: use Completed, Stopped or Failed.", nameof(release));

    for (int attempt = 0; attempt <= MaxConflictRetries; attempt++)
    {
      OrchestrationRunRecord? current = await _repository.GetAsync(release.OrchestratorName, cancellationToken);
      if (current is not { Status: RunStatus.Running } || current.InstanceId != release.InstanceId)
        return false;

      DateTimeOffset now = _timeProvider.GetUtcNow();
      OrchestrationRunRecord ended = current with
      {
        Status = release.Status,
        LeaseExpiresAt = now,
        FinishedAt = now,
        Error = release.Error,
      };

      if (await _repository.TryReplaceAsync(ended, current.ETag!, cancellationToken))
        return true;
    }

    throw new InvalidOperationException(
      $"Could not release the run lease for '{release.OrchestratorName}': lost {MaxConflictRetries + 1} write races in a row.");
  }
}
