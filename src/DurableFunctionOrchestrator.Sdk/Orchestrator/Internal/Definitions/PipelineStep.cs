namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>
/// A declared step: what it calls, how its input is built, and its options. Run by <see cref="StepStage{TInput}"/>.
/// </summary>
internal sealed record PipelineStep<TInput>(
  StepHandle Handle,
  Func<OrchestrationState<TInput>, object?> InputSelector,
  StepOptions<TInput> Options);
