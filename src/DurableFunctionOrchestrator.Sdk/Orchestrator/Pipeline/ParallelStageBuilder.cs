using DurableFunctionOrchestrator.Sdk.Internal;

namespace DurableFunctionOrchestrator.Sdk;

/// <summary>Builder for the steps of a parallel (fan-out/fan-in) stage.</summary>
public sealed class ParallelStageBuilder<TInput>
{
  private readonly List<PipelineStep<TInput>> _steps = [];
  private readonly StepFactory<TInput> _stepFactory;

  internal ParallelStageBuilder(StepFactory<TInput> stepFactory) => _stepFactory = stepFactory;

  /// <summary>Adds a concurrent step that calls the activity or orchestrator identified by <paramref name="step"/>.</summary>
  public ParallelStageBuilder<TInput> Step(StepHandle step, Func<OrchestrationState<TInput>, object?>? input = null, Action<StepOptions<TInput>>? configure = null)
  {
    _steps.Add(_stepFactory.Create(step, input, configure));
    return this;
  }

  internal IReadOnlyList<PipelineStep<TInput>> Build() =>
    _steps.Count == 0
      ? throw new InvalidOperationException("A parallel stage must contain at least one step.")
      : _steps.ToArray();
}
