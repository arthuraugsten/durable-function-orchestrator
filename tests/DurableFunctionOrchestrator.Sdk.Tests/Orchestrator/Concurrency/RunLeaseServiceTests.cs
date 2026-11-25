using DurableFunctionOrchestrator.Sdk.Tests.Fakes;

namespace DurableFunctionOrchestrator.Sdk.Tests;

public sealed class RunLeaseServiceTests
{
  private const string Orchestrator = "NightlySync";
  private const int OneHour = 3600;

  private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private readonly InMemoryOrchestrationRunRepository _repository = new();
  private readonly ManualTimeProvider _clock = new(Start);
  private readonly RunLeaseService _leases;

  public RunLeaseServiceTests()
  {
    _leases = new RunLeaseService(_repository, _clock);
  }

  [Fact]
  public async Task First_run_acquires_the_lease_and_stores_a_running_record_keyed_by_orchestrator_name()
  {
    RunLeaseResult result = await _leases.TryAcquireAsync(new(Orchestrator, "run-1", OneHour));

    Assert.True(result.Acquired);
    OrchestrationRunRecord stored = (await _repository.GetAsync(Orchestrator))!;
    Assert.Equal((Orchestrator, "run-1", RunStatus.Running), (stored.OrchestratorName, stored.InstanceId, stored.Status));
    Assert.Equal(Start, stored.StartedAt);
    Assert.Equal(Start.AddHours(1), stored.LeaseExpiresAt);
    Assert.NotNull(stored.ETag);
  }

  [Fact]
  public async Task Another_run_is_rejected_while_the_lease_is_held_and_sees_the_holder()
  {
    await _leases.TryAcquireAsync(new(Orchestrator, "run-1", OneHour));
    _clock.Advance(TimeSpan.FromMinutes(59));

    RunLeaseResult result = await _leases.TryAcquireAsync(new(Orchestrator, "run-2", OneHour));

    Assert.False(result.Acquired);
    Assert.Equal("run-1", result.Record?.InstanceId);
    Assert.Equal("run-1", (await _repository.GetAsync(Orchestrator))!.InstanceId);
  }

  [Fact]
  public async Task Acquiring_again_with_the_holding_instance_succeeds_without_writing()
  {
    await _leases.TryAcquireAsync(new(Orchestrator, "run-1", OneHour));
    string? etag = (await _repository.GetAsync(Orchestrator))!.ETag;

    RunLeaseResult retry = await _leases.TryAcquireAsync(new(Orchestrator, "run-1", OneHour));

    Assert.True(retry.Acquired);
    Assert.Equal(etag, (await _repository.GetAsync(Orchestrator))!.ETag);
  }

  [Fact]
  public async Task Expired_lease_is_taken_over_by_the_next_run()
  {
    await _leases.TryAcquireAsync(new(Orchestrator, "abandoned", OneHour));
    _clock.Advance(TimeSpan.FromHours(1));

    RunLeaseResult result = await _leases.TryAcquireAsync(new(Orchestrator, "run-2", OneHour));

    Assert.True(result.Acquired);
    OrchestrationRunRecord stored = (await _repository.GetAsync(Orchestrator))!;
    Assert.Equal(("run-2", RunStatus.Running), (stored.InstanceId, stored.Status));
    Assert.Equal(Start.AddHours(1), stored.StartedAt);
  }

  [Theory]
  [InlineData(RunStatus.Completed, null)]
  [InlineData(RunStatus.Stopped, null)]
  [InlineData(RunStatus.Failed, "Step 'Sync' failed: timeout")]
  public async Task Release_records_how_the_run_ended_and_frees_the_orchestrator(RunStatus status, string? error)
  {
    await _leases.TryAcquireAsync(new(Orchestrator, "run-1", OneHour));
    _clock.Advance(TimeSpan.FromMinutes(10));

    bool released = await _leases.ReleaseAsync(new(Orchestrator, "run-1", status, error));

    Assert.True(released);
    OrchestrationRunRecord stored = (await _repository.GetAsync(Orchestrator))!;
    Assert.Equal((status, error), (stored.Status, stored.Error));
    Assert.Equal(Start.AddMinutes(10), stored.FinishedAt);
    Assert.True((await _leases.TryAcquireAsync(new(Orchestrator, "run-2", OneHour))).Acquired);
  }

  [Fact]
  public async Task Release_is_idempotent_and_ignores_runs_that_no_longer_hold_the_lease()
  {
    await _leases.TryAcquireAsync(new(Orchestrator, "run-1", OneHour));
    Assert.True(await _leases.ReleaseAsync(new(Orchestrator, "run-1", RunStatus.Completed)));

    // The release activity ran again.
    Assert.False(await _leases.ReleaseAsync(new(Orchestrator, "run-1", RunStatus.Failed, "late")));
    Assert.Equal(RunStatus.Completed, (await _repository.GetAsync(Orchestrator))!.Status);

    // A run whose lease expired and was taken over must not free the new holder.
    await _leases.TryAcquireAsync(new(Orchestrator, "run-2", OneHour));
    Assert.False(await _leases.ReleaseAsync(new(Orchestrator, "run-1", RunStatus.Completed)));
    Assert.Equal(("run-2", RunStatus.Running), ((await _repository.GetAsync(Orchestrator))!.InstanceId, (await _repository.GetAsync(Orchestrator))!.Status));
  }

  [Fact]
  public async Task Renewal_extends_the_holder_lease_from_now()
  {
    await _leases.TryAcquireAsync(new(Orchestrator, "run-1", OneHour));
    _clock.Advance(TimeSpan.FromMinutes(20));

    RunLeaseResult renewed = await _leases.RenewAsync(new(Orchestrator, "run-1", OneHour));

    Assert.True(renewed.Acquired);
    OrchestrationRunRecord stored = (await _repository.GetAsync(Orchestrator))!;
    Assert.Equal(Start.AddMinutes(80), stored.LeaseExpiresAt);
    Assert.Equal(Start, stored.StartedAt);
  }

  [Fact]
  public async Task Renewal_never_takes_a_lease_the_run_does_not_hold()
  {
    // Never acquired.
    Assert.False((await _leases.RenewAsync(new(Orchestrator, "run-1", OneHour))).Acquired);
    Assert.Null(await _repository.GetAsync(Orchestrator));

    // Expired and taken over by another run.
    await _leases.TryAcquireAsync(new(Orchestrator, "run-1", OneHour));
    _clock.Advance(TimeSpan.FromHours(2));
    await _leases.TryAcquireAsync(new(Orchestrator, "run-2", OneHour));
    RunLeaseResult lost = await _leases.RenewAsync(new(Orchestrator, "run-1", OneHour));
    Assert.False(lost.Acquired);
    Assert.Equal("run-2", lost.Record?.InstanceId);

    // Already released, even though the orchestrator is now free.
    await _leases.ReleaseAsync(new(Orchestrator, "run-2", RunStatus.Completed));
    Assert.False((await _leases.RenewAsync(new(Orchestrator, "run-2", OneHour))).Acquired);
    Assert.Equal(RunStatus.Completed, (await _repository.GetAsync(Orchestrator))!.Status);
  }

  [Fact]
  public async Task Of_many_runs_racing_for_a_free_orchestrator_exactly_one_acquires_the_lease()
  {
    RunLeaseResult[] results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i =>
      Task.Run(() => _leases.TryAcquireAsync(new(Orchestrator, $"run-{i}", OneHour)))));

    RunLeaseResult winner = Assert.Single(results, result => result.Acquired);
    Assert.All(results.Where(result => !result.Acquired), loser => Assert.Equal(winner.Record?.InstanceId, loser.Record?.InstanceId));
    Assert.Equal(winner.Record?.InstanceId, (await _repository.GetAsync(Orchestrator))!.InstanceId);
  }

  [Fact]
  public async Task Leases_of_different_orchestrators_are_independent()
  {
    Assert.True((await _leases.TryAcquireAsync(new("NightlySync", "run-1", OneHour))).Acquired);
    Assert.True((await _leases.TryAcquireAsync(new("WeeklyReport", "run-2", OneHour))).Acquired);
  }

  [Fact]
  public async Task Invalid_requests_are_rejected()
  {
    await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _leases.TryAcquireAsync(new(Orchestrator, "run-1", 59)));
    await Assert.ThrowsAsync<ArgumentException>(() => _leases.TryAcquireAsync(new(" ", "run-1", OneHour)));
    await Assert.ThrowsAsync<ArgumentException>(() => _leases.ReleaseAsync(new(Orchestrator, "run-1", RunStatus.Running)));
  }

  [Fact]
  public async Task Repository_replace_fails_when_the_etag_changed()
  {
    var record = new OrchestrationRunRecord(Orchestrator, "run-1", RunStatus.Running, Start, Start.AddHours(1));
    Assert.True(await _repository.TryInsertAsync(record));
    Assert.False(await _repository.TryInsertAsync(record));

    string etag = (await _repository.GetAsync(Orchestrator))!.ETag!;
    Assert.True(await _repository.TryReplaceAsync(record with { Status = RunStatus.Completed }, etag));
    Assert.False(await _repository.TryReplaceAsync(record with { Status = RunStatus.Failed }, etag));
    Assert.Equal(RunStatus.Completed, (await _repository.GetAsync(Orchestrator))!.Status);
  }
}
