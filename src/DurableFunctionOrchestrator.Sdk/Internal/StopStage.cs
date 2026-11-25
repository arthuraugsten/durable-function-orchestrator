using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Sdk.Internal;

internal sealed class StopStage<TInput> : IPipelineStage<TInput>
{
  private readonly Func<OrchestrationState<TInput>, bool> _predicate;
  private readonly string _reason;

  public StopStage(Func<OrchestrationState<TInput>, bool> predicate, string reason)
  {
    _predicate = predicate;
    _reason = reason;
  }

  public string Description => $"StopIf({_reason})";

  public Task ExecuteAsync(PipelineRuntime<TInput> runtime)
  {
    if (_predicate(runtime.State))
    {
      runtime.Logger.LogInformation("Stopping pipeline: {Reason}", _reason);
      runtime.State.Stop(_reason);
    }

    return Task.CompletedTask;
  }
}
