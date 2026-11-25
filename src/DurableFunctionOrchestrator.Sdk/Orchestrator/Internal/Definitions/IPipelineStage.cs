namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>
/// One stage of a declared pipeline. The builder creates stages; <see cref="PipelineRunner{TInput}"/>
/// runs them in order.
/// </summary>
internal interface IPipelineStage<TInput>
{
  /// <summary>Shown in progress and logs.</summary>
  string Description { get; }

  Task ExecuteAsync(PipelineRuntime<TInput> runtime);
}
