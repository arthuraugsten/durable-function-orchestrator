using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

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
    // Steps catch task failures and return them as outcomes, so WhenAll waits for every
    // branch. Outcomes are then applied in declaration order, which keeps replay deterministic
    // and lets failure handlers see every branch that did succeed (for compensation).
    StepOutcome[] outcomes = await Task.WhenAll(_steps.Select(step => ExecuteStepAsync(step, runtime)));

    StepFailedException? firstFatalFailure = null;
    for (int i = 0; i < _steps.Count; i++)
    {
      PipelineStep<TInput> step = _steps[i];
      StepOutcome outcome = outcomes[i];

      switch (outcome.Kind)
      {
        case StepOutcomeKind.Succeeded:
          runtime.State.RecordSuccess(step.Handle, outcome.Result);
          break;

        case StepOutcomeKind.Skipped:
          runtime.State.RecordSkipped(step.Handle);
          break;

        case StepOutcomeKind.Failed when step.Options.ShouldContinueOnError:
          runtime.State.RecordFailure(step.Handle, outcome.Failure!.FailureDetails);
          runtime.Logger.LogWarning(
            "Step '{StepName}' failed and is marked ContinueOnError: {Error}",
            step.Handle.Name,
            outcome.Failure.FailureDetails.ErrorMessage);
          break;

        case StepOutcomeKind.Failed:
          firstFatalFailure ??= new StepFailedException(step.Handle, outcome.Failure!);
          break;
      }
    }

    if (firstFatalFailure is not null)
      throw firstFatalFailure;
  }

  /// <summary>Evaluates the step's condition, builds its input and calls it. Task failures become outcomes.</summary>
  private static async Task<StepOutcome> ExecuteStepAsync(PipelineStep<TInput> step, PipelineRuntime<TInput> runtime)
  {
    StepHandle handle = step.Handle;

    if (step.Options.Condition is { } condition && !condition(runtime.State))
    {
      runtime.Logger.LogInformation("Skipping step '{StepName}': condition not met.", handle.Name);
      return StepOutcome.Skipped();
    }

    object? input = step.InputSelector(runtime.State);
    runtime.Logger.LogInformation("Running step '{StepName}' ({Kind} '{TargetName}').", handle.Name, handle.Kind, handle.TargetName);

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
