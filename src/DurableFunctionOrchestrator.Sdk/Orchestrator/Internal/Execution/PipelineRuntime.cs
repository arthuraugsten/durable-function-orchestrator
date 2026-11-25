using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>What a stage needs while it runs: the Durable Task context, the state, defaults and a replay-safe logger.</summary>
internal sealed record PipelineRuntime<TInput>(TaskOrchestrationContext Context, OrchestrationState<TInput> State, TaskOptions? DefaultTaskOptions, ILogger Logger);
