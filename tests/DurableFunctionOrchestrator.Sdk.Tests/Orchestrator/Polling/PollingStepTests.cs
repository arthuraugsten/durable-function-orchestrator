using DurableFunctionOrchestrator.Sdk.Tests.Fakes;
using Microsoft.DurableTask;

namespace DurableFunctionOrchestrator.Sdk.Tests;

public sealed class PollingStepTests
{
  private static readonly StepHandle<string> StartJob = Steps.Activity<string>("StartJob");
  private static readonly StepHandle<PollStatus<int>> CheckJob = Steps.Activity<PollStatus<int>>("CheckJob");

  [Fact]
  public async Task Polling_step_starts_the_operation_then_checks_it_after_each_interval_until_it_completes()
  {
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 5);
    int checks = 0;
    var context = new FakeOrchestrationContext()
      .Returns("StartJob", "job-1")
      .Handle("CheckJob", _ => ++checks < 3 ? PollStatus<int>.Pending() : PollStatus<int>.Completed(42));
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline.Step(job));

    OrchestrationState<string> state = await orchestrator.RunAsync(context, "payload");

    Assert.Equal(["StartJob", "CheckJob", "CheckJob", "CheckJob"], context.CallNames);
    Assert.Equal("payload", context.Calls[0].Input);
    Assert.All(context.Calls.Skip(1), call => Assert.Equal("job-1", call.Input));
    Assert.Equal([5d, 10d, 15d], context.TimerSeconds);
    Assert.Equal(42, state.GetResult(job));
    Assert.Equal([job], state.CompletedSteps);
  }

  [Fact]
  public async Task Polling_result_feeds_later_steps_like_any_other_step_result()
  {
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 1);
    var report = Steps.Activity<string>("Report");
    var context = new FakeOrchestrationContext()
      .Returns("StartJob", "job-1")
      .Returns("CheckJob", PollStatus<int>.Completed(7))
      .Handle("Report", input => $"result {input}");
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline
      .Step(job)
      .Step(report, state => state.GetResult(job)));

    OrchestrationState<string> state = await orchestrator.RunAsync(context, "x");

    Assert.Equal("result 7", state.GetResult(report));
  }

  [Fact]
  public async Task Retry_after_longer_than_the_interval_lengthens_the_next_wait_and_a_shorter_one_is_ignored()
  {
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 10);
    int checks = 0;
    var context = new FakeOrchestrationContext()
      .Returns("StartJob", "job-1")
      .Handle("CheckJob", _ => ++checks switch
      {
        1 => PollStatus<int>.Pending(retryAfterSeconds: 30),
        2 => PollStatus<int>.Pending(retryAfterSeconds: 3),
        _ => PollStatus<int>.Completed(1),
      });
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline.Step(job));

    await orchestrator.RunAsync(context, "x");

    // Waits: the interval (10s), then Retry-After (30s), then the interval again (3s < 10s is ignored).
    Assert.Equal([10d, 40d, 50d], context.TimerSeconds);
  }

  [Fact]
  public async Task Backoff_multiplies_the_interval_after_each_pending_check_up_to_the_maximum()
  {
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 5, backoffCoefficient: 2, maxIntervalSeconds: 30);
    int checks = 0;
    var context = new FakeOrchestrationContext()
      .Returns("StartJob", "job-1")
      .Handle("CheckJob", _ => ++checks < 6 ? PollStatus<int>.Pending() : PollStatus<int>.Completed(1));
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline.Step(job));

    await orchestrator.RunAsync(context, "x");

    // Waits: 5, 10, 20, 30 (capped), 30, 30.
    Assert.Equal([5d, 15d, 35d, 65d, 95d, 125d], context.TimerSeconds);
  }

  [Fact]
  public async Task Operation_taking_almost_twelve_hours_completes_with_a_bounded_number_of_checks()
  {
    const int TwelveHours = 12 * 60 * 60;
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 30, timeoutSeconds: TwelveHours, backoffCoefficient: 2, maxIntervalSeconds: Steps.MaxPollingIntervalSeconds);
    DateTime readyAt = FakeOrchestrationContext.Start.AddHours(11).AddMinutes(50);
    var context = new FakeOrchestrationContext();
    context
      .Returns("StartJob", "job-1")
      .Handle("CheckJob", _ => context.CurrentUtcDateTime >= readyAt ? PollStatus<int>.Completed(42) : PollStatus<int>.Pending());
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline.Step(job));

    OrchestrationState<string> state = await orchestrator.RunAsync(context, "x");

    Assert.Equal(42, state.GetResult(job));

    // Waits of 30s, 1m, 2m, 4m, then every 8 minutes: under 100 checks, and noticed within 8 minutes.
    Assert.InRange(context.TimerSeconds.Count, 80, 100);
    double readySeconds = (readyAt - FakeOrchestrationContext.Start).TotalSeconds;
    Assert.InRange(context.TimerSeconds[^1], readySeconds, readySeconds + Steps.MaxPollingIntervalSeconds);
  }

  [Fact]
  public async Task Operation_still_pending_after_twelve_hours_times_out_exactly_at_the_deadline()
  {
    const int TwelveHours = 12 * 60 * 60;
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 60, timeoutSeconds: TwelveHours, backoffCoefficient: 1.5, maxIntervalSeconds: 480);
    var context = new FakeOrchestrationContext()
      .Returns("StartJob", "job-1")
      .Returns("CheckJob", PollStatus<int>.Pending());
    var orchestrator = new TestOrchestrator<string>(
      pipeline => pipeline.Step(job),
      onStepFailed: (_, state, _) => Task.FromResult(state));

    await orchestrator.RunAsync(context, "x");

    Assert.Equal(TwelveHours, context.TimerSeconds[^1]);
    Assert.Equal(PollingErrorTypes.TimedOut, orchestrator.HandledFailure!.FailureDetails.ErrorType);
  }

  [Fact]
  public void Twelve_hour_schedules_within_the_check_budget_are_accepted()
  {
    const int TwelveHours = 12 * 60 * 60;

    // Fixed 60s: 720 checks. Fixed 8 minutes: 90 checks. Backoff from 5s to 8 minutes: about 97 checks.
    Steps.Polling(StartJob, CheckJob, intervalSeconds: 60, timeoutSeconds: TwelveHours);
    Steps.Polling(StartJob, CheckJob, intervalSeconds: Steps.MaxPollingIntervalSeconds, timeoutSeconds: TwelveHours);
    Steps.Polling(StartJob, CheckJob, intervalSeconds: 5, timeoutSeconds: TwelveHours, backoffCoefficient: 2, maxIntervalSeconds: Steps.MaxPollingIntervalSeconds);
  }

  [Theory]
  [InlineData(1, 12 * 60 * 60)]
  [InlineData(30, 12 * 60 * 60)]
  [InlineData(60, 5 * 24 * 60 * 60)]
  public void Schedule_that_could_exceed_the_check_budget_is_rejected_with_a_backoff_that_fits(int intervalSeconds, int timeoutSeconds)
  {
    ArgumentException error = Assert.Throws<ArgumentException>(() => Steps.Polling(StartJob, CheckJob, intervalSeconds, timeoutSeconds));

    Assert.Contains($"more than {Steps.MaxPollingChecks} checks", error.Message);

    // The suggested backoff must itself be accepted.
    int suggestedMax = int.Parse(System.Text.RegularExpressions.Regex.Match(error.Message, @"maxIntervalSeconds: (\d+)").Groups[1].Value);
    Steps.Polling(StartJob, CheckJob, intervalSeconds, timeoutSeconds, backoffCoefficient: 2, maxIntervalSeconds: suggestedMax);
  }

  [Fact]
  public void Timeout_too_long_to_fit_the_budget_even_at_the_maximum_interval_is_rejected()
  {
    int longest = Steps.MaxPollingChecks * Steps.MaxPollingIntervalSeconds;

    // Exactly 1,000 checks, 8 minutes apart: the longest timeout that fits.
    Steps.Polling(StartJob, CheckJob, intervalSeconds: Steps.MaxPollingIntervalSeconds, timeoutSeconds: longest);

    ArgumentException error = Assert.Throws<ArgumentException>(
      () => Steps.Polling(StartJob, CheckJob, Steps.MaxPollingIntervalSeconds, timeoutSeconds: longest + 1));
    Assert.Contains($"the longest timeout is {longest} seconds", error.Message);
  }

  [Theory]
  [InlineData(481, null)]
  [InlineData(60, 481)]
  public void Intervals_longer_than_eight_minutes_are_rejected(int intervalSeconds, int? maxIntervalSeconds)
  {
    ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
      () => Steps.Polling(StartJob, CheckJob, intervalSeconds, backoffCoefficient: maxIntervalSeconds is null ? 1 : 2, maxIntervalSeconds: maxIntervalSeconds));
    Assert.Contains("480 seconds (8 minutes)", error.Message);
  }

  [Fact]
  public async Task Retry_after_longer_than_eight_minutes_is_capped_at_eight_minutes()
  {
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 60);
    int checks = 0;
    var context = new FakeOrchestrationContext()
      .Returns("StartJob", "job-1")
      .Handle("CheckJob", _ => ++checks < 2 ? PollStatus<int>.Pending(retryAfterSeconds: 3600) : PollStatus<int>.Completed(1));
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline.Step(job));

    await orchestrator.RunAsync(context, "x");

    // Waits: the interval (60s), then 8 minutes instead of the hour the server asked for.
    Assert.Equal([60d, 60d + Steps.MaxPollingIntervalSeconds], context.TimerSeconds);
  }

  [Fact]
  public void Polling_without_a_timeout_has_no_check_budget()
  {
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 1);

    Assert.Equal(StepKind.Polling, job.Kind);
  }

  [Theory]
  [InlineData(0.5)]
  [InlineData(double.NaN)]
  [InlineData(double.PositiveInfinity)]
  public void Backoff_coefficient_below_one_or_not_finite_is_rejected(double backoffCoefficient)
  {
    Assert.Throws<ArgumentOutOfRangeException>(() => Steps.Polling(StartJob, CheckJob, 5, backoffCoefficient: backoffCoefficient, maxIntervalSeconds: 60));
  }

  [Fact]
  public void Backoff_requires_a_maximum_interval_of_at_least_the_interval()
  {
    Assert.Throws<ArgumentException>(() => Steps.Polling(StartJob, CheckJob, 5, backoffCoefficient: 2));
    Assert.Throws<ArgumentOutOfRangeException>(() => Steps.Polling(StartJob, CheckJob, 10, backoffCoefficient: 2, maxIntervalSeconds: 5));
  }

  [Fact]
  public async Task Polling_step_fails_with_the_reported_error_when_the_operation_fails()
  {
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 5);
    var context = new FakeOrchestrationContext()
      .Returns("StartJob", "job-1")
      .Returns("CheckJob", PollStatus<int>.Failed("Carrier rejected the address."));
    var orchestrator = new TestOrchestrator<string>(
      pipeline => pipeline.Step(job),
      onStepFailed: (_, state, _) => Task.FromResult(state));

    await orchestrator.RunAsync(context, "x");

    StepFailedException failure = Assert.IsType<StepFailedException>(orchestrator.HandledFailure);
    Assert.Equal(job, failure.Step);
    Assert.Equal(PollingErrorTypes.OperationFailed, failure.FailureDetails.ErrorType);
    Assert.Equal("Carrier rejected the address.", failure.FailureDetails.ErrorMessage);
    Assert.Equal(["StartJob", "CheckJob"], context.CallNames);
  }

  [Fact]
  public async Task Polling_step_times_out_when_the_operation_is_still_pending_at_the_deadline()
  {
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 10, timeoutSeconds: 25);
    var context = new FakeOrchestrationContext()
      .Returns("StartJob", "job-1")
      .Returns("CheckJob", PollStatus<int>.Pending());
    var orchestrator = new TestOrchestrator<string>(
      pipeline => pipeline.Step(job),
      onStepFailed: (_, state, _) => Task.FromResult(state));

    await orchestrator.RunAsync(context, "x");

    // The last wait is shortened so the final check happens exactly at the deadline.
    Assert.Equal([10d, 20d, 25d], context.TimerSeconds);
    StepFailedException failure = Assert.IsType<StepFailedException>(orchestrator.HandledFailure);
    Assert.Equal(job, failure.Step);
    Assert.Equal(PollingErrorTypes.TimedOut, failure.FailureDetails.ErrorType);
    Assert.Contains("25 second(s)", failure.FailureDetails.ErrorMessage);
  }

  [Fact]
  public async Task Timers_firing_early_end_polling_at_the_deadline_without_spinning()
  {
    // As with the Azure Storage provider, whose whole-second queue delays fire timers up to a second early.
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 10, timeoutSeconds: 25);
    var context = new FakeOrchestrationContext()
      .TimersFireEarly(TimeSpan.FromSeconds(1))
      .Returns("StartJob", "job-1")
      .Returns("CheckJob", PollStatus<int>.Pending());
    var orchestrator = new TestOrchestrator<string>(
      pipeline => pipeline.Step(job),
      onStepFailed: (_, state, _) => Task.FromResult(state));

    await orchestrator.RunAsync(context, "x");

    // Requested fire times; the last wait is shortened to the deadline and its check is the final one, even though
    // that timer fired at 24s. Re-reading the clock there used to schedule timer after timer to 25s, each firing at once.
    Assert.Equal([10d, 19d, 25d], context.TimerSeconds);
    Assert.Equal(3, context.CallNames.Count(name => name == "CheckJob"));
    Assert.Equal(PollingErrorTypes.TimedOut, orchestrator.HandledFailure!.FailureDetails.ErrorType);
  }

  [Fact]
  public async Task Operation_completing_on_an_early_final_check_is_not_a_timeout()
  {
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 10, timeoutSeconds: 25);
    int checks = 0;
    var context = new FakeOrchestrationContext()
      .TimersFireEarly(TimeSpan.FromSeconds(1))
      .Returns("StartJob", "job-1")
      .Handle("CheckJob", _ => ++checks < 3 ? PollStatus<int>.Pending() : PollStatus<int>.Completed(5));
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline.Step(job));

    OrchestrationState<string> state = await orchestrator.RunAsync(context, "x");

    Assert.Equal(5, state.GetResult(job));
  }

  [Fact]
  public async Task Operation_completing_on_the_check_at_the_deadline_is_not_a_timeout()
  {
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 10, timeoutSeconds: 20);
    int checks = 0;
    var context = new FakeOrchestrationContext()
      .Returns("StartJob", "job-1")
      .Handle("CheckJob", _ => ++checks < 2 ? PollStatus<int>.Pending() : PollStatus<int>.Completed(5));
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline.Step(job));

    OrchestrationState<string> state = await orchestrator.RunAsync(context, "x");

    Assert.Equal(5, state.GetResult(job));
  }

  [Fact]
  public async Task Polling_failure_marked_ContinueOnError_is_recorded_and_the_pipeline_continues()
  {
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 5);
    var next = Steps.Activity<string>("Next");
    var context = new FakeOrchestrationContext()
      .Returns("StartJob", "job-1")
      .Returns("CheckJob", PollStatus<int>.Failed("nope"))
      .Returns("Next", "ran");
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline
      .Step(job, configure: step => step.ContinueOnError())
      .Step(next));

    OrchestrationState<string> state = await orchestrator.RunAsync(context, "x");

    Assert.Equal("nope", state.Failures[job].ErrorMessage);
    Assert.Equal("ran", state.GetResult(next));
  }

  [Fact]
  public async Task Start_failure_fails_the_polling_step_without_checking()
  {
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 5);
    var context = new FakeOrchestrationContext().Fails("StartJob", "HTTP 500");
    var orchestrator = new TestOrchestrator<string>(
      pipeline => pipeline.Step(job),
      onStepFailed: (_, state, _) => Task.FromResult(state));

    await orchestrator.RunAsync(context, "x");

    StepFailedException failure = Assert.IsType<StepFailedException>(orchestrator.HandledFailure);
    Assert.Equal(job, failure.Step);
    Assert.Equal("HTTP 500", failure.FailureDetails.ErrorMessage);
    Assert.Equal(["StartJob"], context.CallNames);
    Assert.Empty(context.TimerSeconds);
  }

  [Fact]
  public async Task Check_failure_fails_the_polling_step()
  {
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 5);
    var context = new FakeOrchestrationContext()
      .Returns("StartJob", "job-1")
      .Fails("CheckJob", "status endpoint unreachable");
    var orchestrator = new TestOrchestrator<string>(
      pipeline => pipeline.Step(job),
      onStepFailed: (_, state, _) => Task.FromResult(state));

    await orchestrator.RunAsync(context, "x");

    StepFailedException failure = Assert.IsType<StepFailedException>(orchestrator.HandledFailure);
    Assert.Equal(job, failure.Step);
    Assert.Equal("status endpoint unreachable", failure.FailureDetails.ErrorMessage);
  }

  [Fact]
  public async Task Task_options_apply_to_every_start_and_check_call()
  {
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 5);
    int checks = 0;
    var context = new FakeOrchestrationContext()
      .Returns("StartJob", "job-1")
      .Handle("CheckJob", _ => ++checks < 2 ? PollStatus<int>.Pending() : PollStatus<int>.Completed(1));
    var defaults = TaskOptions.FromRetryPolicy(new RetryPolicy(3, TimeSpan.FromSeconds(1)));
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline.Step(job), defaultTaskOptions: defaults);

    await orchestrator.RunAsync(context, "x");

    Assert.Equal(3, context.Calls.Count);
    Assert.All(context.Calls, call => Assert.Same(defaults, call.Options));
  }

  [Fact]
  public async Task Start_and_check_can_be_sub_orchestrations()
  {
    var start = Steps.SubOrchestration<string>("StartOrchestrator");
    var check = Steps.SubOrchestration<PollStatus<int>>("CheckOrchestrator");
    var job = Steps.Polling(start, check, intervalSeconds: 5);
    var context = new FakeOrchestrationContext()
      .Returns("StartOrchestrator", "job-1")
      .Returns("CheckOrchestrator", PollStatus<int>.Completed(9));
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline.Step(job));

    OrchestrationState<string> state = await orchestrator.RunAsync(context, "x");

    Assert.Equal(9, state.GetResult(job));
    Assert.All(context.Calls, call => Assert.Equal(StepKind.SubOrchestration, call.Kind));
  }

  [Fact]
  public async Task Polling_steps_in_a_parallel_stage_poll_concurrently()
  {
    var first = Steps.Polling(StartJob, CheckJob, intervalSeconds: 5, name: "First");
    var second = Steps.Polling(StartJob, CheckJob, intervalSeconds: 5, name: "Second");
    var context = new FakeOrchestrationContext()
      .Handle("StartJob", input => $"job-{input}")
      .Handle("CheckJob", input => PollStatus<int>.Completed(input is "job-1" ? 1 : 2));
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline
      .Parallel(parallel => parallel
        .Step(first, _ => 1)
        .Step(second, _ => 2)));

    OrchestrationState<string> state = await orchestrator.RunAsync(context, "x");

    Assert.Equal(1, state.GetResult(first));
    Assert.Equal(2, state.GetResult(second));
    Assert.Equal([first, second], state.CompletedSteps);
  }

  [Fact]
  public void Polling_step_name_defaults_to_the_start_step_and_kind_is_polling()
  {
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 5);

    Assert.Equal("StartJob", job.Name);
    Assert.Equal(StepKind.Polling, job.Kind);
    Assert.Equal("StartJob", job.TargetName);
  }

  [Fact]
  public void Polling_handles_compare_by_value_including_their_polling_configuration()
  {
    Assert.Equal(Steps.Polling(StartJob, CheckJob, 5, 60), Steps.Polling(StartJob, CheckJob, 5, 60));
    Assert.NotEqual(Steps.Polling(StartJob, CheckJob, 5), Steps.Polling(StartJob, CheckJob, 10));
    Assert.NotEqual(
      Steps.Polling(StartJob, CheckJob, 5, backoffCoefficient: 2, maxIntervalSeconds: 60),
      Steps.Polling(StartJob, CheckJob, 5, backoffCoefficient: 2, maxIntervalSeconds: 120));
    Assert.NotEqual<StepHandle>(Steps.Polling(StartJob, CheckJob, 5), StartJob);
  }

  [Theory]
  [InlineData(0, null)]
  [InlineData(-1, null)]
  [InlineData(10, 5)]
  public void Invalid_polling_interval_or_timeout_is_rejected(int intervalSeconds, int? timeoutSeconds)
  {
    Assert.Throws<ArgumentOutOfRangeException>(() => Steps.Polling(StartJob, CheckJob, intervalSeconds, timeoutSeconds));
  }

  [Fact]
  public void Polling_steps_cannot_be_nested()
  {
    var inner = Steps.Polling(StartJob, CheckJob, intervalSeconds: 5);

    Assert.Throws<ArgumentException>(() => Steps.Polling(inner, Steps.Activity<PollStatus<int>>("Check"), intervalSeconds: 5));
  }

  [Fact]
  public async Task Duplicate_polling_step_names_are_rejected_when_the_pipeline_is_built()
  {
    var job = Steps.Polling(StartJob, CheckJob, intervalSeconds: 5);
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline
      .Step(StartJob)
      .Step(job));

    await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.RunAsync(new FakeOrchestrationContext(), "x"));
  }
}
