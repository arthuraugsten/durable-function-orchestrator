namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>Creates pipeline steps and enforces unique step names across the whole pipeline.</summary>
internal sealed class StepFactory<TInput>
{
  private readonly HashSet<string> _stepNames = new(StringComparer.Ordinal);

  public PipelineStep<TInput> Create(StepHandle step, Func<OrchestrationState<TInput>, object?>? input, Action<StepOptions<TInput>>? configure)
  {
    ArgumentNullException.ThrowIfNull(step);

    if (!_stepNames.Add(step.Name))
      throw new InvalidOperationException($"Duplicate step name '{step.Name}'. To call the same target more than once, give each handle a unique name, e.g. Steps.Activity<T>(\"{step.TargetName}\", name: \"...\").");

    var options = new StepOptions<TInput>();
    configure?.Invoke(options);

    return new PipelineStep<TInput>(step, input ?? (state => state.Input), options);
  }
}
