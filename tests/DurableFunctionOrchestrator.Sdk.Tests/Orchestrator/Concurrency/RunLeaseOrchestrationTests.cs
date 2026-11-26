using DurableFunctionOrchestrator.Sdk.Tests.Fakes;
using Microsoft.DurableTask;

namespace DurableFunctionOrchestrator.Sdk.Tests;

/// <summary>
/// Run leases end to end: the orchestrator calls the lease activities, which run the real
/// <see cref="RunLeaseService"/> over the in-memory repository. Every test runs in virtual time
/// (<see cref="FakeOrchestrationContext.Run{TResult}"/>), because renewal races each stage against a timer.
/// </summary>
public sealed class RunLeaseOrchestrationTests
{
  private const string Acquire = RunLeaseOptions.DefaultAcquireActivityName;
  private const string Renew = RunLeaseOptions.DefaultRenewActivityName;
  private const string Release = RunLeaseOptions.DefaultReleaseActivityName;

  // A one-hour lease, renewed every 20 minutes (a third of the lease).
  private static readonly RunLeaseOptions Lease = new(leaseSeconds: 3600);

  private static readonly StepHandle<int> A = Steps.Activity<int>("A");

  private readonly InMemoryOrchestrationRunRepository _repository = new();

  private static string Name => new FakeOrchestrationContext().Name.Name;

  // A context whose lease activities run the real service on the orchestration's virtual clock.
  private FakeOrchestrationContext NewContext(string acquire = Acquire, string renew = Renew, string release = Release)
  {
    var context = new FakeOrchestrationContext();
    var leases = new RunLeaseService(_repository, new OrchestrationTimeProvider(context));
    return context
      .Handle(acquire, input => leases.TryAcquireAsync((RunLeaseRequest)input!).GetAwaiter().GetResult())
      .Handle(renew, input => leases.RenewAsync((RunLeaseRequest)input!).GetAwaiter().GetResult())
      .Handle(release, input => leases.ReleaseAsync((RunLeaseRelease)input!).GetAwaiter().GetResult());
  }

  private static OrchestrationState<string> Run(FakeOrchestrationContext context, TestOrchestrator<string> orchestrator) =>
    context.Run(() => orchestrator.RunAsync(context, "x"));

  private OrchestrationRunRecord Stored() => _repository.GetAsync(Name).GetAwaiter().GetResult()!;

  private Task HoldLeaseAsOtherRunAsync() =>
    new RunLeaseService(_repository, new OrchestrationTimeProvider(new FakeOrchestrationContext()))
      .TryAcquireAsync(new(Name, "other-region-run", 3600));

  [Fact]
  public void Lease_is_acquired_before_the_first_stage_and_released_as_completed_at_the_end()
  {
    FakeOrchestrationContext context = NewContext().Returns("A", 1);

    Run(context, new TestOrchestrator<string>(pipeline => pipeline.Step(A), runLease: Lease));

    Assert.Equal([Acquire, "A", Release], context.CallNames);
    var request = (RunLeaseRequest)context.Calls[0].Input!;
    Assert.Equal((Name, context.InstanceId, 3600), (request.OrchestratorName, request.InstanceId, request.LeaseSeconds));
    Assert.Equal((context.InstanceId, RunStatus.Completed, (string?)null), (Stored().InstanceId, Stored().Status, Stored().Error));
  }

  [Fact]
  public async Task Rejected_run_skips_its_pipeline_and_leaves_the_holder_lease_alone()
  {
    await HoldLeaseAsOtherRunAsync();
    FakeOrchestrationContext context = NewContext().Returns("A", 1);
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline.Step(A), runLease: Lease, returnStateWhenRejected: true);

    OrchestrationState<string> state = Run(context, orchestrator);

    Assert.Equal([Acquire], context.CallNames);
    Assert.Equal("other-region-run", orchestrator.RejectedBy?.InstanceId);
    Assert.Empty(state.CompletedSteps);
    Assert.Equal("Rejected", context.StatusHistory[^1].Status);
    Assert.Equal(("other-region-run", RunStatus.Running), (Stored().InstanceId, Stored().Status));
  }

  [Fact]
  public async Task Rejection_fails_the_orchestration_by_default()
  {
    await HoldLeaseAsOtherRunAsync();
    FakeOrchestrationContext context = NewContext();

    ConcurrentRunRejectedException error = Assert.Throws<ConcurrentRunRejectedException>(
      () => Run(context, new TestOrchestrator<string>(pipeline => pipeline.Step(A), runLease: Lease)));

    Assert.Equal("other-region-run", error.Current.InstanceId);
    Assert.Contains("already running as instance 'other-region-run'", error.Message);
  }

  [Fact]
  public void Unhandled_step_failure_releases_the_lease_as_failed_and_still_fails_the_orchestration()
  {
    FakeOrchestrationContext context = NewContext().Fails("A", "database timeout");

    Assert.Throws<StepFailedException>(() => Run(context, new TestOrchestrator<string>(pipeline => pipeline.Step(A), runLease: Lease)));

    Assert.Equal([Acquire, "A", Release], context.CallNames);
    Assert.Equal(RunStatus.Failed, Stored().Status);
    Assert.Contains("database timeout", Stored().Error);
  }

  [Fact]
  public void Compensated_failure_releases_after_the_failure_handler_and_is_recorded_as_failed()
  {
    FakeOrchestrationContext context = NewContext().Fails("A").Returns("Undo", null);
    var orchestrator = new TestOrchestrator<string>(
      pipeline => pipeline.Step(A),
      onStepFailed: async (ctx, state, _) =>
      {
        await ctx.CallActivityAsync("Undo");
        return state;
      },
      runLease: Lease);

    Run(context, orchestrator);

    Assert.Equal([Acquire, "A", "Undo", Release], context.CallNames);
    Assert.Equal(RunStatus.Failed, Stored().Status);
  }

  [Fact]
  public void Stopped_run_is_recorded_as_stopped()
  {
    Run(NewContext(), new TestOrchestrator<string>(pipeline => pipeline.StopIf(_ => true, "Nothing to do."), runLease: Lease));

    Assert.Equal(RunStatus.Stopped, Stored().Status);
  }

  [Fact]
  public void Failed_release_is_logged_and_does_not_change_the_run_result()
  {
    FakeOrchestrationContext context = NewContext().Returns("A", 7).Fails(Release, "database down");

    OrchestrationState<string> state = Run(context, new TestOrchestrator<string>(pipeline => pipeline.Step(A), runLease: Lease));

    Assert.Equal(7, state.GetResult(A));
    Assert.Equal([Acquire, "A", Release], context.CallNames);
  }

  [Fact]
  public void Long_stage_renews_the_lease_every_renewal_interval_until_it_finishes()
  {
    FakeOrchestrationContext context = NewContext().Returns("A", 1).Takes("A", TimeSpan.FromHours(3));

    Run(context, new TestOrchestrator<string>(pipeline => pipeline.Step(A), runLease: Lease));

    // Every 20 minutes while the 3-hour activity runs; it completes at 3h, before a renewal due at the same moment.
    Assert.Equal([1200d, 2400, 3600, 4800, 6000, 7200, 8400, 9600], context.CallSeconds(Renew));
    Assert.Equal([Acquire, "A"], context.CallNames.Where(name => name != Renew).Take(2));
    Assert.Equal(Release, context.CallNames[^1]);
    Assert.Equal(RunStatus.Completed, Stored().Status);
  }

  [Fact]
  public void Renewal_timers_that_fire_early_renew_once_each_without_spinning()
  {
    // Each renewal timer fires a second early, as with the Azure Storage provider. The keeper renews when its timer
    // fires instead of re-reading the clock, so an early timer means a slightly early renewal, never a burst of them.
    FakeOrchestrationContext context = NewContext().Returns("A", 1).Takes("A", TimeSpan.FromHours(1)).TimersFireEarly(TimeSpan.FromSeconds(1));

    Run(context, new TestOrchestrator<string>(pipeline => pipeline.Step(A), runLease: Lease));

    Assert.Equal([1199d, 2398, 3597], context.CallSeconds(Renew));
    Assert.Equal(RunStatus.Completed, Stored().Status);
  }

  [Fact]
  public void Twelve_hour_polling_step_keeps_its_lease_for_the_whole_run()
  {
    const int TwelveHours = 12 * 3600;
    var job = Steps.Polling(
      Steps.Activity<string>("StartJob"),
      Steps.Activity<PollStatus<int>>("CheckJob"),
      intervalSeconds: 60,
      timeoutSeconds: TwelveHours,
      backoffCoefficient: 2,
      maxIntervalSeconds: Steps.MaxPollingIntervalSeconds);
    var lease = new RunLeaseOptions(leaseSeconds: 30 * 60);
    FakeOrchestrationContext context = NewContext().Returns("StartJob", "job-1");
    DateTime readyAt = FakeOrchestrationContext.Start.AddHours(11).AddMinutes(55);
    context.Handle("CheckJob", _ => context.CurrentUtcDateTime >= readyAt ? PollStatus<int>.Completed(42) : PollStatus<int>.Pending());
    var expiries = new List<DateTimeOffset>();
    context.Handle(Release, input =>
    {
      expiries.Add(Stored().LeaseExpiresAt);
      return new RunLeaseService(_repository, new OrchestrationTimeProvider(context)).ReleaseAsync((RunLeaseRelease)input!).GetAwaiter().GetResult();
    });

    OrchestrationState<string> state = Run(context, new TestOrchestrator<string>(pipeline => pipeline.Step(job), runLease: lease));

    Assert.Equal(42, state.GetResult(job));

    // Renewed every 10 minutes for the whole poll, so the lease never lapsed and ran past the finish.
    IReadOnlyList<double> renewals = context.CallSeconds(Renew);
    Assert.Equal(71, renewals.Count);
    Assert.All(renewals.Zip(renewals.Skip(1)), pair => Assert.Equal(600, pair.Second - pair.First));
    Assert.True(expiries.Single() > new DateTimeOffset(readyAt));
    Assert.Equal(RunStatus.Completed, Stored().Status);
  }

  [Fact]
  public void Polling_waits_longer_than_the_lease_are_covered_by_renewals_during_each_wait()
  {
    // Each polling wait (8 minutes, the maximum) is eight times the lease (60 seconds, the minimum, renewed every 20s).
    var job = Steps.Polling(
      Steps.Activity<string>("StartJob"),
      Steps.Activity<PollStatus<int>>("CheckJob"),
      intervalSeconds: Steps.MaxPollingIntervalSeconds,
      timeoutSeconds: 3600);
    var lease = new RunLeaseOptions(leaseSeconds: RunLeaseOptions.MinLeaseSeconds);
    FakeOrchestrationContext context = NewContext().Returns("StartJob", "job-1");
    var leases = new RunLeaseService(_repository, new OrchestrationTimeProvider(context));
    DateTime readyAt = FakeOrchestrationContext.Start.AddMinutes(40);
    var lapses = new List<string>();

    void AssertLeaseStillValid(string when)
    {
      OrchestrationRunRecord current = Stored();
      if (current.LeaseExpiresAt <= new DateTimeOffset(context.CurrentUtcDateTime))
        lapses.Add($"{when} at {(context.CurrentUtcDateTime - FakeOrchestrationContext.Start).TotalSeconds}s: lease expired at {current.LeaseExpiresAt:O}");
    }

    context.Handle(Renew, input =>
    {
      AssertLeaseStillValid("renewal");
      return leases.RenewAsync((RunLeaseRequest)input!).GetAwaiter().GetResult();
    });
    context.Handle("CheckJob", _ =>
    {
      AssertLeaseStillValid("check");
      return context.CurrentUtcDateTime >= readyAt ? PollStatus<int>.Completed(42) : PollStatus<int>.Pending();
    });

    OrchestrationState<string> state = Run(context, new TestOrchestrator<string>(pipeline => pipeline.Step(job), runLease: lease));

    Assert.Equal(42, state.GetResult(job));
    Assert.Equal([480d, 960, 1440, 1920, 2400], context.CallSeconds("CheckJob"));

    // The lease never lapsed: not at any renewal, not at any check.
    Assert.Empty(lapses);

    // Renewals ran inside every 8-minute wait, every 20 seconds, not only between checks.
    IReadOnlyList<double> renewals = context.CallSeconds(Renew);
    double[] checkTimes = [0, .. context.CallSeconds("CheckJob")];
    foreach ((double from, double to) in checkTimes.Zip(checkTimes.Skip(1)))
      Assert.Equal(23, renewals.Count(at => at > from && at < to));
    Assert.All(renewals.Zip(renewals.Skip(1)), pair => Assert.Equal(20, pair.Second - pair.First));

    Assert.Equal(RunStatus.Completed, Stored().Status);
  }

  [Fact]
  public void Run_stops_with_RunLeaseLostException_when_another_run_took_the_lease()
  {
    FakeOrchestrationContext context = NewContext().Returns("A", 1).Takes("A", TimeSpan.FromHours(3));
    // An outage stopped renewals long enough for the lease to expire and another region's run to take it.
    context.Handle(Renew, _ =>
    {
      OrchestrationRunRecord current = Stored();
      _repository.TryReplaceAsync(current with { InstanceId = "other-region-run" }, current.ETag!).GetAwaiter().GetResult();
      return new RunLeaseService(_repository, new OrchestrationTimeProvider(context)).RenewAsync(new(Name, context.InstanceId, 3600)).GetAwaiter().GetResult();
    });

    RunLeaseLostException lost = Assert.Throws<RunLeaseLostException>(
      () => Run(context, new TestOrchestrator<string>(pipeline => pipeline.Step(A), runLease: Lease)));

    Assert.Equal("other-region-run", lost.Current?.InstanceId);
    Assert.Equal([1200d], context.CallSeconds(Renew));
    Assert.Equal(("other-region-run", RunStatus.Running), (Stored().InstanceId, Stored().Status));
  }

  [Fact]
  public void One_failed_renewal_is_tolerated_and_the_next_one_keeps_the_lease()
  {
    FakeOrchestrationContext context = NewContext().Returns("A", 1).Takes("A", TimeSpan.FromMinutes(50));
    var leases = new RunLeaseService(_repository, new OrchestrationTimeProvider(context));
    int renewals = 0;
    context.Handle(Renew, input => ++renewals == 1
      ? throw new InvalidOperationException("database briefly down")
      : leases.RenewAsync((RunLeaseRequest)input!).GetAwaiter().GetResult());

    Run(context, new TestOrchestrator<string>(pipeline => pipeline.Step(A), runLease: Lease));

    Assert.Equal([1200d, 2400], context.CallSeconds(Renew));
    Assert.Equal(RunStatus.Completed, Stored().Status);
  }

  [Fact]
  public void Run_stops_when_renewals_keep_failing_until_the_lease_could_expire()
  {
    FakeOrchestrationContext context = NewContext().Returns("A", 1).Takes("A", TimeSpan.FromHours(3)).Fails(Renew, "database down");

    RunLeaseLostException lost = Assert.Throws<RunLeaseLostException>(
      () => Run(context, new TestOrchestrator<string>(pipeline => pipeline.Step(A), runLease: Lease)));

    // Tolerated at 20 minutes; at 40 minutes the next attempt (60 minutes) would be too late.
    Assert.Equal([1200d, 2400], context.CallSeconds(Renew));
    Assert.Contains("database down", lost.Message);
    Assert.Equal(RunStatus.Failed, Stored().Status);
  }

  [Fact]
  public void Lease_activities_use_the_default_task_options_unless_the_lease_sets_its_own()
  {
    var defaults = TaskOptions.FromRetryPolicy(new RetryPolicy(3, TimeSpan.FromSeconds(1)));
    var own = TaskOptions.FromRetryPolicy(new RetryPolicy(5, TimeSpan.FromSeconds(2)));

    FakeOrchestrationContext first = NewContext().Returns("A", 1);
    Run(first, new TestOrchestrator<string>(pipeline => pipeline.Step(A), defaults, runLease: Lease));

    FakeOrchestrationContext second = NewContext().Returns("A", 1).Takes("A", TimeSpan.FromMinutes(30));
    Run(second, new TestOrchestrator<string>(pipeline => pipeline.Step(A), defaults, runLease: Lease with { TaskOptions = own }));

    Assert.All(first.Calls, call => Assert.Same(defaults, call.Options));
    Assert.All(second.Calls.Where(call => call.Name != "A"), call => Assert.Same(own, call.Options));
    Assert.Same(defaults, second.Calls.Single(call => call.Name == "A").Options);
    Assert.Contains(Renew, second.CallNames);
  }

  [Fact]
  public void Lease_activity_names_can_be_customised()
  {
    FakeOrchestrationContext context = NewContext("TakeLock", "KeepLock", "FreeLock").Returns("A", 1).Takes("A", TimeSpan.FromMinutes(30));
    var lease = Lease with { AcquireActivityName = "TakeLock", RenewActivityName = "KeepLock", ReleaseActivityName = "FreeLock" };

    Run(context, new TestOrchestrator<string>(pipeline => pipeline.Step(A), runLease: lease));

    Assert.Equal(["TakeLock", "A", "KeepLock", "FreeLock"], context.CallNames);
  }

  [Fact]
  public void Without_a_run_lease_no_lease_activities_are_called()
  {
    FakeOrchestrationContext context = new FakeOrchestrationContext().Returns("A", 1).Takes("A", TimeSpan.FromHours(3));

    context.Run(() => new TestOrchestrator<string>(pipeline => pipeline.Step(A)).RunAsync(context, "x"));

    Assert.Equal(["A"], context.CallNames);
  }

  [Fact]
  public void Lease_and_renewal_lengths_are_validated()
  {
    Assert.Throws<ArgumentOutOfRangeException>(() => new RunLeaseOptions(leaseSeconds: 59));
    Assert.Throws<ArgumentOutOfRangeException>(() => new RunLeaseOptions(3600) { RenewalSeconds = 3600 });
    Assert.Throws<ArgumentOutOfRangeException>(() => new RunLeaseOptions(3600) { RenewalSeconds = 0 });
    Assert.Equal(1200, new RunLeaseOptions(3600).RenewalSeconds);
    Assert.Equal(300, new RunLeaseOptions(3600) { RenewalSeconds = 300 }.RenewalSeconds);
  }
}
