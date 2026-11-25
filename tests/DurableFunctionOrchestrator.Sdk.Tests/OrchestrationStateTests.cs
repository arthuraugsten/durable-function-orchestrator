using DurableFunctionOrchestrator.Sdk.Tests.Fakes;

namespace DurableFunctionOrchestrator.Sdk.Tests;

public sealed class OrchestrationStateTests
{
  private static readonly StepHandle<int> A = Steps.Activity<int>("A");

  [Fact]
  public async Task GetResult_for_a_step_outside_the_pipeline_explains_why()
  {
    OrchestrationState<string> state = await RunAsync(pipeline => pipeline.Step(A), new FakeOrchestrationContext().Returns("A", 1));

    var error = Assert.Throws<InvalidOperationException>(() => state.GetResult(Steps.Activity<int>("Missing")));

    Assert.Contains("Step 'Missing' has no result", error.Message);
  }

  [Fact]
  public async Task Handles_compare_by_value_so_an_equivalent_handle_reads_the_same_result()
  {
    OrchestrationState<string> state = await RunAsync(pipeline => pipeline.Step(A), new FakeOrchestrationContext().Returns("A", 1));

    Assert.Equal(1, state.GetResult(Steps.Activity<int>("A")));
  }

  [Fact]
  public async Task Handle_with_the_same_name_but_another_result_type_does_not_match()
  {
    OrchestrationState<string> state = await RunAsync(pipeline => pipeline.Step(A), new FakeOrchestrationContext().Returns("A", 1));

    Assert.False(state.TryGetResult(Steps.Activity<string>("A"), out _));
    Assert.NotEqual<StepHandle>(A, Steps.Activity<string>("A"));
    Assert.NotEqual<StepHandle>(A, Steps.SubOrchestration<int>("A"));
  }

  [Fact]
  public async Task GetResult_returns_null_results_for_reference_types()
  {
    var nullable = Steps.Activity<string?>("Nullable");

    OrchestrationState<string> state = await RunAsync(pipeline => pipeline.Step(nullable), new FakeOrchestrationContext().Returns("Nullable", null));

    Assert.Null(state.GetResult(nullable));
  }

  [Fact]
  public async Task TryGetResult_reports_whether_the_step_completed()
  {
    var skipped = Steps.Activity<int>("Skipped");

    OrchestrationState<string> state = await RunAsync(
      pipeline => pipeline
        .Step(A)
        .Step(skipped, configure: step => step.When(_ => false)),
      new FakeOrchestrationContext().Returns("A", 1));

    Assert.True(state.TryGetResult(A, out int value));
    Assert.Equal(1, value);
    Assert.False(state.TryGetResult(skipped, out _));
  }

  [Fact]
  public async Task InstanceId_comes_from_the_context()
  {
    OrchestrationState<string> state = await RunAsync(pipeline => pipeline.Step(A), new FakeOrchestrationContext().Returns("A", 1));

    Assert.Equal("test-instance", state.InstanceId);
  }

  [Fact]
  public void Handles_default_their_name_to_the_target_and_show_it_in_ToString()
  {
    StepHandle<int> named = Steps.Activity<int>("Target", name: "Custom");

    Assert.Equal(("A", "A"), (A.Name, A.TargetName));
    Assert.Equal(("Custom", "Target"), (named.Name, named.TargetName));
    Assert.Equal("Custom", named.ToString());
  }

  private static Task<OrchestrationState<string>> RunAsync(Action<OrchestrationPipelineBuilder<string>> configure, FakeOrchestrationContext context) =>
    new TestOrchestrator<string>(configure).RunAsync(context, "input");
}
