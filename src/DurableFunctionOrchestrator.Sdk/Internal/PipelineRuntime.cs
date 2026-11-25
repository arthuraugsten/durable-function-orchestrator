using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Sdk.Internal;

internal sealed record PipelineRuntime<TInput>(TaskOrchestrationContext Context, OrchestrationState<TInput> State, TaskOptions? DefaultTaskOptions, ILogger Logger);

internal interface IPipelineStage<TInput>
{
  string Description { get; }

  Task ExecuteAsync(PipelineRuntime<TInput> runtime);
}
