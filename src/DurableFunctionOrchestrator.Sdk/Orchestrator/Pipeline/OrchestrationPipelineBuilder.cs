using DurableFunctionOrchestrator.Sdk.Internal;

namespace DurableFunctionOrchestrator.Sdk;

/// <summary>
/// Fluent builder used by <see cref="ActivityOrchestrator{TInput, TOutput}"/> implementations to
/// declare their steps. Stages run in the order they are added.
/// </summary>
/// <remarks>
/// Steps are identified by <see cref="StepHandle"/>s created with <see cref="Steps"/>. In every
/// <c>Step</c> method, <c>input</c> builds the input sent to the activity or orchestrator from the
/// current state (it defaults to the orchestration input), and <c>configure</c> sets optional step
/// options: condition, retry and error handling.
/// </remarks>
public sealed class OrchestrationPipelineBuilder<TInput>
{
  private readonly List<IPipelineStage<TInput>> _stages = [];
  private readonly StepFactory<TInput> _stepFactory = new();

  internal OrchestrationPipelineBuilder()
  {
  }

  /// <summary>Adds a step that calls the activity or orchestrator identified by <paramref name="step"/>.</summary>
  public OrchestrationPipelineBuilder<TInput> Step(StepHandle step, Func<OrchestrationState<TInput>, object?>? input = null, Action<StepOptions<TInput>>? configure = null)
  {
    _stages.Add(new StepStage<TInput>([_stepFactory.Create(step, input, configure)]));
    return this;
  }

  /// <summary>
  /// Adds a fan-out/fan-in stage: every step in it runs concurrently, and the pipeline continues
  /// once all of them finish. Steps in the same parallel stage cannot read each other's results.
  /// </summary>
  public OrchestrationPipelineBuilder<TInput> Parallel(Action<ParallelStageBuilder<TInput>> configure)
  {
    ArgumentNullException.ThrowIfNull(configure);

    var parallel = new ParallelStageBuilder<TInput>(_stepFactory);
    configure(parallel);
    _stages.Add(new StepStage<TInput>(parallel.Build()));
    return this;
  }

  /// <summary>
  /// Adds a checkpoint that ends the pipeline early when <paramref name="predicate"/> is true.
  /// <c>BuildOutput</c> is still called, with <see cref="OrchestrationState{TInput}.IsStopped"/> set.
  /// </summary>
  public OrchestrationPipelineBuilder<TInput> StopIf(Func<OrchestrationState<TInput>, bool> predicate, string reason)
  {
    ArgumentNullException.ThrowIfNull(predicate);
    ArgumentException.ThrowIfNullOrWhiteSpace(reason);

    _stages.Add(new StopStage<TInput>(predicate, reason));
    return this;
  }

  internal IReadOnlyList<IPipelineStage<TInput>> Build() =>
    _stages.Count == 0
      ? throw new InvalidOperationException("The orchestration pipeline must contain at least one stage.")
      : _stages.ToArray();
}
