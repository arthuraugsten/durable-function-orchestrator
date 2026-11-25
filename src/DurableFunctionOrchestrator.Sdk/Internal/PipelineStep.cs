using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Sdk.Internal;

internal sealed class PipelineStep<TInput>
{
  private readonly Func<OrchestrationState<TInput>, object?> _inputSelector;
  private readonly StepOptions<TInput> _options;

  public PipelineStep(StepHandle handle, Func<OrchestrationState<TInput>, object?> inputSelector, StepOptions<TInput> options)
  {
    Handle = handle;
    _inputSelector = inputSelector;
    _options = options;
  }

  public StepHandle Handle { get; }

  public bool ContinueOnError => _options.ShouldContinueOnError;

  public async Task<StepOutcome> ExecuteAsync(PipelineRuntime<TInput> runtime)
  {
    if (_options.Condition is { } condition && !condition(runtime.State))
    {
      runtime.Logger.LogInformation("Skipping step '{StepName}': condition not met.", Handle.Name);
      return StepOutcome.Skipped();
    }

    object? input = _inputSelector(runtime.State);
    runtime.Logger.LogInformation("Running step '{StepName}' ({Kind} '{TargetName}').", Handle.Name, Handle.Kind, Handle.TargetName);

    try
    {
      object? result = await Handle.InvokeAsync(runtime.Context, input, _options.TaskOptions ?? runtime.DefaultTaskOptions);
      return StepOutcome.Succeeded(result);
    }
    catch (TaskFailedException ex)
    {
      return StepOutcome.Failed(ex);
    }
  }
}

internal readonly record struct StepOutcome(StepOutcomeKind Kind, object? Result, TaskFailedException? Failure)
{
  public static StepOutcome Succeeded(object? result) => new(StepOutcomeKind.Succeeded, result, null);

  public static StepOutcome Skipped() => new(StepOutcomeKind.Skipped, null, null);

  public static StepOutcome Failed(TaskFailedException failure) => new(StepOutcomeKind.Failed, null, failure);
}

internal enum StepOutcomeKind
{
  Succeeded,
  Skipped,
  Failed,
}
