using Microsoft.DurableTask;

namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>Runs one step, or several steps concurrently (fan-out/fan-in).</summary>
internal sealed class StepStage<TInput> : IPipelineStage<TInput>
{
  private readonly IReadOnlyList<PipelineStep<TInput>> _steps;

  public StepStage(IReadOnlyList<PipelineStep<TInput>> steps)
  {
    _steps = steps;
    Description = steps.Count == 1
      ? steps[0].Handle.Name
      : $"Parallel({string.Join(", ", steps.Select(s => s.Handle.Name))})";
  }

  public string Description { get; }

  public async Task ExecuteAsync(PipelineRuntime<TInput> runtime)
  {
    // Most stages hold a single step: await it directly, with no fan-out allocations.
    if (_steps.Count == 1)
    {
      StepOutcome outcome = await ExecuteStepAsync(_steps[0], runtime);
      if (Record(_steps[0], outcome, runtime) is { } failure)
        throw failure;

      return;
    }

    // Steps catch task failures and return them as outcomes, so WhenAll waits for every
    // branch. Outcomes are then applied in declaration order, which keeps replay deterministic
    // and lets failure handlers see every branch that did succeed (for compensation).
    var branches = new Task<StepOutcome>[_steps.Count];
    for (int i = 0; i < branches.Length; i++)
      branches[i] = ExecuteStepAsync(_steps[i], runtime);

    StepOutcome[] outcomes = await Task.WhenAll(branches);

    StepFailedException? firstFatalFailure = null;
    for (int i = 0; i < outcomes.Length; i++)
    {
      // Record every branch, even after a failure, so compensation can see all the successes.
      StepFailedException? failure = Record(_steps[i], outcomes[i], runtime);
      firstFatalFailure ??= failure;
    }

    if (firstFatalFailure is not null)
      throw firstFatalFailure;
  }

  /// <summary>Records a step's outcome in the state. Returns the failure to raise, if the step failed fatally.</summary>
  private static StepFailedException? Record(PipelineStep<TInput> step, StepOutcome outcome, PipelineRuntime<TInput> runtime)
  {
    switch (outcome.Kind)
    {
      case StepOutcomeKind.Succeeded:
        runtime.State.RecordSuccess(step.Handle, outcome.Result);
        return null;

      case StepOutcomeKind.Skipped:
        runtime.State.RecordSkipped(step.Handle);
        return null;

      case StepOutcomeKind.Failed when step.Options.ShouldContinueOnError:
        runtime.State.RecordFailure(step.Handle, outcome.Failure!.FailureDetails);
        SdkLog.StepFailedAndContinued(runtime.Logger, step.Handle.Name, outcome.Failure.FailureDetails.ErrorMessage);
        return null;

      default:
        return new StepFailedException(step.Handle, outcome.Failure!);
    }
  }

  /// <summary>Evaluates the step's condition, builds its input and calls it. Task failures become outcomes.</summary>
  private static async Task<StepOutcome> ExecuteStepAsync(PipelineStep<TInput> step, PipelineRuntime<TInput> runtime)
  {
    StepHandle handle = step.Handle;

    if (step.Options.Condition is { } condition && !condition(runtime.State))
    {
      SdkLog.StepSkipped(runtime.Logger, handle.Name);
      return StepOutcome.Skipped();
    }

    object? input = step.InputSelector(runtime.State);
    SdkLog.StepRunning(runtime.Logger, handle.Name, handle.Kind, handle.TargetName);

    try
    {
      object? result = await handle.InvokeAsync(runtime.Context, input, step.Options.TaskOptions ?? runtime.DefaultTaskOptions, runtime.Logger);
      return StepOutcome.Succeeded(result);
    }
    catch (TaskFailedException ex)
    {
      return StepOutcome.Failed(ex);
    }
  }
}
