using DurableFunctionOrchestrator.Sdk.Tests.Fakes;
using Microsoft.DurableTask;

namespace DurableFunctionOrchestrator.Sdk.Tests;

public sealed class ActivityOrchestratorTests
{
  [Fact]
  public async Task Sequential_steps_run_in_order_and_feed_results_forward()
  {
    var doubled = Steps.Activity<int>("Double");
    var plusOne = Steps.Activity<int>("AddOne");
    var context = new FakeOrchestrationContext()
      .Handle("Double", input => (int)input! * 2)
      .Handle("AddOne", input => (int)input! + 1);
    var orchestrator = new TestOrchestrator<int>(pipeline => pipeline
      .Step(doubled)
      .Step(plusOne, state => state.GetResult(doubled)));

    OrchestrationState<int> state = await orchestrator.RunAsync(context, 5);

    Assert.Equal(["Double", "AddOne"], context.CallNames);
    Assert.Equal(10, state.GetResult(doubled));
    Assert.Equal(11, state.GetResult(plusOne));
    Assert.Equal([doubled, plusOne], state.CompletedSteps);
  }

  [Fact]
  public async Task Step_input_defaults_to_the_orchestration_input()
  {
    var context = new FakeOrchestrationContext().Returns("Echo", "ok");
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline.Step(Steps.Activity<string>("Echo")));

    await orchestrator.RunAsync(context, "order-42");

    Assert.Equal("order-42", Assert.Single(context.Calls).Input);
  }

  [Fact]
  public async Task ExecuteAsync_reads_the_input_from_the_context()
  {
    var context = new FakeOrchestrationContext(input: "from-context").Returns("Echo", "ok");
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline.Step(Steps.Activity<string>("Echo")));

    OrchestrationState<string> state = await orchestrator.ExecuteFromContextAsync(context);

    Assert.Equal("from-context", state.Input);
    Assert.Equal("from-context", Assert.Single(context.Calls).Input);
  }

  [Fact]
  public async Task Step_without_result_is_recorded_as_completed()
  {
    var notify = Steps.Activity("Notify");
    var context = new FakeOrchestrationContext().Returns("Notify", null);
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline.Step(notify));

    OrchestrationState<string> state = await orchestrator.RunAsync(context, "x");

    Assert.Equal([notify], state.CompletedSteps);
  }

  [Fact]
  public async Task Parallel_stage_runs_every_step_before_the_next_stage()
  {
    var a = Steps.Activity<int>("A");
    var b = Steps.Activity<int>("B");
    var sum = Steps.Activity<int>("Sum");
    var context = new FakeOrchestrationContext()
      .Returns("A", 1)
      .Returns("B", 2)
      .Handle("Sum", input => (int)input!);
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline
      .Parallel(parallel => parallel
        .Step(a)
        .Step(b))
      .Step(sum, state => state.GetResult(a) + state.GetResult(b)));

    OrchestrationState<string> state = await orchestrator.RunAsync(context, "x");

    Assert.Equal(3, state.GetResult(sum));
    Assert.Equal("Sum", context.CallNames[^1]);
    Assert.Equal(["A", "B"], context.CallNames.Take(2).Order());
  }

  [Fact]
  public async Task Step_whose_condition_is_false_is_skipped_and_never_called()
  {
    var always = Steps.Activity<int>("Always");
    var conditional = Steps.Activity<int>("OnlyIfTrue");
    var context = new FakeOrchestrationContext().Returns("Always", 1);
    var orchestrator = new TestOrchestrator<bool>(pipeline => pipeline
      .Step(always)
      .Step(conditional, configure: step => step.When(state => state.Input)));

    OrchestrationState<bool> state = await orchestrator.RunAsync(context, false);

    Assert.Equal(["Always"], context.CallNames);
    Assert.Equal([conditional], state.SkippedSteps);
    Assert.False(state.TryGetResult(conditional, out _));
  }

  [Fact]
  public async Task StopIf_ends_the_pipeline_and_still_builds_the_output()
  {
    var validate = Steps.Activity<bool>("Validate");
    var context = new FakeOrchestrationContext().Returns("Validate", false);
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline
      .Step(validate)
      .StopIf(state => !state.GetResult(validate), "Invalid input.")
      .Step(Steps.Activity<int>("NeverRuns")));

    OrchestrationState<string> state = await orchestrator.RunAsync(context, "x");

    Assert.True(state.IsStopped);
    Assert.Equal("Invalid input.", state.StopReason);
    Assert.Equal(["Validate"], context.CallNames);
  }

  [Fact]
  public async Task StopIf_with_false_predicate_lets_the_pipeline_continue()
  {
    var validate = Steps.Activity<bool>("Validate");
    var context = new FakeOrchestrationContext().Returns("Validate", true).Returns("Next", 1);
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline
      .Step(validate)
      .StopIf(state => !state.GetResult(validate), "Invalid input.")
      .Step(Steps.Activity<int>("Next")));

    OrchestrationState<string> state = await orchestrator.RunAsync(context, "x");

    Assert.False(state.IsStopped);
    Assert.Equal(["Validate", "Next"], context.CallNames);
  }

  [Fact]
  public async Task Failed_step_rethrows_by_default()
  {
    var second = Steps.Activity<int>("Second");
    var context = new FakeOrchestrationContext().Returns("First", 1).Fails("Second", "payment declined").Returns("Third", 3);
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline
      .Step(Steps.Activity<int>("First"))
      .Step(second)
      .Step(Steps.Activity<int>("Third")));

    StepFailedException failure = await Assert.ThrowsAsync<StepFailedException>(() => orchestrator.RunAsync(context, "x"));

    Assert.Equal(second, failure.Step);
    Assert.Equal(StepKind.Activity, failure.Step.Kind);
    Assert.Equal("payment declined", failure.FailureDetails.ErrorMessage);
    Assert.IsType<TaskFailedException>(failure.InnerException);
    Assert.Equal(["First", "Second"], context.CallNames);
  }

  [Fact]
  public async Task Failed_step_can_be_handled_with_compensation_and_a_fallback_output()
  {
    var reserve = Steps.Activity<string>("Reserve");
    var charge = Steps.Activity<string>("Charge");
    var context = new FakeOrchestrationContext()
      .Returns("Reserve", "res-1")
      .Fails("Charge")
      .Returns("Release", null);
    var orchestrator = new TestOrchestrator<string>(
      pipeline => pipeline
        .Step(reserve)
        .Step(charge),
      onStepFailed: async (ctx, state, failure) =>
      {
        await ctx.CallActivityAsync("Release", state.GetResult(reserve));
        return state;
      });

    await orchestrator.RunAsync(context, "x");

    Assert.Equal(charge, orchestrator.HandledFailure?.Step);
    Assert.Equal(["Reserve", "Charge", "Release"], context.CallNames);
    Assert.Equal("res-1", context.Calls[^1].Input);
  }

  [Fact]
  public async Task Parallel_failure_is_raised_after_every_branch_finishes_so_successes_can_be_compensated()
  {
    var a = Steps.Activity<string>("A");
    var b = Steps.Activity<string>("B");
    var context = new FakeOrchestrationContext().Fails("A").Returns("B", "b-result").Fails("C");
    var orchestrator = new TestOrchestrator<string>(
      pipeline => pipeline.Parallel(parallel => parallel
        .Step(a)
        .Step(b)
        .Step(Steps.Activity<string>("C"))),
      onStepFailed: (_, state, _) => Task.FromResult(state));

    OrchestrationState<string> state = await orchestrator.RunAsync(context, "x");

    // The first failure in declaration order is reported, deterministically.
    Assert.Equal(a, orchestrator.HandledFailure?.Step);
    Assert.Equal("b-result", state.GetResult(b));
    Assert.Equal(3, context.Calls.Count);
  }

  [Fact]
  public async Task Parallel_branches_after_a_fatal_failure_are_still_recorded_including_continued_failures()
  {
    var fatal = Steps.Activity<string>("Fatal");
    var optional = Steps.Activity<string>("Optional");
    var ok = Steps.Activity<string>("Ok");
    var context = new FakeOrchestrationContext().Fails("Fatal").Fails("Optional", "optional down").Returns("Ok", "ok-result");
    var orchestrator = new TestOrchestrator<string>(
      pipeline => pipeline.Parallel(parallel => parallel
        .Step(fatal)
        .Step(optional, configure: step => step.ContinueOnError())
        .Step(ok)),
      onStepFailed: (_, state, _) => Task.FromResult(state));

    OrchestrationState<string> state = await orchestrator.RunAsync(context, "x");

    Assert.Equal(fatal, orchestrator.HandledFailure?.Step);
    Assert.Equal("optional down", state.Failures[optional].ErrorMessage);
    Assert.Equal("ok-result", state.GetResult(ok));
    Assert.Equal([ok], state.CompletedSteps);
  }

  [Fact]
  public async Task ContinueOnError_records_the_failure_and_continues()
  {
    var notify = Steps.Activity("Notify");
    var next = Steps.Activity<int>("Next");
    var context = new FakeOrchestrationContext().Fails("Notify", "smtp down").Returns("Next", 1);
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline
      .Step(notify, configure: step => step.ContinueOnError())
      .Step(next));

    OrchestrationState<string> state = await orchestrator.RunAsync(context, "x");

    Assert.Null(orchestrator.HandledFailure);
    Assert.Equal("smtp down", state.Failures[notify].ErrorMessage);
    Assert.Equal([next], state.CompletedSteps);
  }

  [Fact]
  public async Task Default_task_options_apply_unless_a_step_overrides_them()
  {
    var defaults = TaskOptions.FromRetryPolicy(new RetryPolicy(3, TimeSpan.FromSeconds(1)));
    var custom = TaskOptions.FromRetryPolicy(new RetryPolicy(5, TimeSpan.FromSeconds(2)));
    var context = new FakeOrchestrationContext().Returns("UsesDefault", 1).Returns("UsesCustom", 2).Returns("UsesRetry", 3);
    var orchestrator = new TestOrchestrator<string>(
      pipeline => pipeline
        .Step(Steps.Activity<int>("UsesDefault"))
        .Step(Steps.Activity<int>("UsesCustom"), configure: step => step.WithOptions(custom))
        .Step(Steps.Activity<int>("UsesRetry"), configure: step => step.WithRetry(new RetryPolicy(7, TimeSpan.FromSeconds(3)))),
      defaultTaskOptions: defaults);

    await orchestrator.RunAsync(context, "x");

    Assert.Same(defaults, context.Calls[0].Options);
    Assert.Same(custom, context.Calls[1].Options);
    Assert.Equal(7, context.Calls[2].Options?.Retry?.Policy?.MaxNumberOfAttempts);
  }

  [Fact]
  public async Task Named_handles_allow_the_same_activity_twice()
  {
    var first = Steps.Activity<int>("Square", name: "First");
    var second = Steps.Activity<int>("Square", name: "Second");
    var context = new FakeOrchestrationContext().Handle("Square", input => (int)input! * (int)input!);
    var orchestrator = new TestOrchestrator<int>(pipeline => pipeline
      .Step(first)
      .Step(second, state => state.GetResult(first)));

    OrchestrationState<int> state = await orchestrator.RunAsync(context, 3);

    Assert.Equal(["Square", "Square"], context.CallNames);
    Assert.Equal(9, state.GetResult(first));
    Assert.Equal(81, state.GetResult(second));
  }

  [Fact]
  public async Task Duplicate_step_names_are_rejected()
  {
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline
      .Step(Steps.Activity<int>("Same"))
      .Parallel(parallel => parallel.Step(Steps.Activity("Same"))));

    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.RunAsync(new FakeOrchestrationContext(), "x"));

    Assert.Contains("Duplicate step name 'Same'", error.Message);
  }

  [Fact]
  public async Task Empty_pipeline_is_rejected() =>
    await Assert.ThrowsAsync<InvalidOperationException>(
      () => new TestOrchestrator<string>(_ => { }).RunAsync(new FakeOrchestrationContext(), "x"));

  [Fact]
  public async Task Empty_parallel_stage_is_rejected() =>
    await Assert.ThrowsAsync<InvalidOperationException>(
      () => new TestOrchestrator<string>(pipeline => pipeline.Parallel(_ => { })).RunAsync(new FakeOrchestrationContext(), "x"));

  [Fact]
  public async Task SubOrchestration_step_calls_the_orchestrator_and_stores_its_result()
  {
    var child = Steps.SubOrchestration<string>("ChildOrchestrator");
    var context = new FakeOrchestrationContext().Handle("ChildOrchestrator", input => $"shipped {input}");
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline.Step(child, state => state.Input.ToUpperInvariant()));

    OrchestrationState<string> state = await orchestrator.RunAsync(context, "order-1");

    TaskCall call = Assert.Single(context.Calls);
    Assert.Equal(StepKind.SubOrchestration, call.Kind);
    Assert.Equal("ORDER-1", call.Input);
    Assert.Equal("shipped ORDER-1", state.GetResult(child));
  }

  [Fact]
  public async Task SubOrchestration_steps_can_run_in_parallel_with_activities()
  {
    var context = new FakeOrchestrationContext().Returns("Child", "c").Returns("Activity", "a");
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline
      .Parallel(parallel => parallel
        .Step(Steps.SubOrchestration<string>("Child"))
        .Step(Steps.Activity<string>("Activity"))));

    await orchestrator.RunAsync(context, "x");

    Assert.Equal(
      [(StepKind.Activity, "Activity"), (StepKind.SubOrchestration, "Child")],
      context.Calls.Select(call => (call.Kind, call.Name)).OrderBy(call => call.Name));
  }

  [Fact]
  public async Task SubOrchestration_failure_reports_its_handle()
  {
    var child = Steps.SubOrchestration("Child");
    var context = new FakeOrchestrationContext().Fails("Child");
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline.Step(child));

    StepFailedException failure = await Assert.ThrowsAsync<StepFailedException>(() => orchestrator.RunAsync(context, "x"));

    Assert.Equal(child, failure.Step);
    Assert.Equal(StepKind.SubOrchestration, failure.Step.Kind);
    Assert.Equal("Child", failure.Step.TargetName);
  }

  [Fact]
  public async Task Progress_is_published_as_custom_status()
  {
    var context = new FakeOrchestrationContext().Returns("A", 1).Returns("B", 2);
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline
      .Step(Steps.Activity<int>("A"))
      .Step(Steps.Activity<int>("B"), configure: step => step.When(_ => false)));

    await orchestrator.RunAsync(context, "x");

    OrchestrationProgress first = context.StatusHistory[0];
    Assert.Equal(("Running", 1, 2, "A"), (first.Status, first.CurrentStage, first.TotalStages, first.Detail));

    OrchestrationProgress last = context.StatusHistory[^1];
    Assert.Equal("Completed", last.Status);
    Assert.Null(last.CurrentStage);
    Assert.Equal(["A"], last.CompletedSteps);
    Assert.Equal(["B"], last.SkippedSteps);
    Assert.Empty(last.FailedSteps);
  }

  [Fact]
  public async Task Progress_lists_steps_that_failed_and_continued_in_declaration_order()
  {
    var context = new FakeOrchestrationContext().Fails("Email").Fails("Sms").Returns("Done", 1);
    var orchestrator = new TestOrchestrator<string>(pipeline => pipeline
      .Parallel(parallel => parallel
        .Step(Steps.Activity("Email"), configure: step => step.ContinueOnError())
        .Step(Steps.Activity("Sms"), configure: step => step.ContinueOnError()))
      .Step(Steps.Activity<int>("Done")));

    await orchestrator.RunAsync(context, "x");

    // Before stage 2 runs, progress already lists both failures.
    Assert.Equal(["Email", "Sms"], context.StatusHistory[1].FailedSteps);

    OrchestrationProgress last = context.StatusHistory[^1];
    Assert.Equal(["Email", "Sms"], last.FailedSteps);
    Assert.Equal(["Done"], last.CompletedSteps);
  }

  [Theory]
  [InlineData(true, "Stopped")]
  [InlineData(false, "Failed")]
  public async Task Final_status_reflects_how_the_pipeline_ended(bool stop, string expectedStatus)
  {
    var context = new FakeOrchestrationContext().Fails("Boom");
    var orchestrator = new TestOrchestrator<string>(
      pipeline => pipeline
        .StopIf(_ => stop, "Stopped by test.")
        .Step(Steps.Activity<int>("Boom")),
      onStepFailed: (_, state, _) => Task.FromResult(state));

    await orchestrator.RunAsync(context, "x");

    Assert.Equal(expectedStatus, context.StatusHistory[^1].Status);
  }
}
