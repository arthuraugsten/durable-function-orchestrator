using System.Diagnostics;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>
/// Calls the target of a step handle through Durable Task. This is the only place that calls
/// activities and sub-orchestrations for steps.
/// </summary>
/// <remarks>
/// Handles reach these methods through <c>StepHandle.InvokeAsync</c>, a one-line virtual hook that
/// supplies the step's result type, which a <see cref="StepHandle"/> reference alone doesn't know.
/// </remarks>
internal static class StepInvoker
{
  /// <summary>Calls a step with no result.</summary>
  public static async Task<object?> InvokeWithoutResultAsync(StepHandle step, TaskOrchestrationContext context, object? input, TaskOptions? options)
  {
    switch (step.Kind)
    {
      case StepKind.Activity:
        await context.CallActivityAsync(step.TargetName, input, options);
        break;

      case StepKind.SubOrchestration:
        await context.CallSubOrchestratorAsync(step.TargetName, input, options);
        break;

      default:
        throw new UnreachableException($"Step '{step.Name}': {step.Kind} steps always have a result type.");
    }

    return null;
  }

  /// <summary>Calls a step that returns a <typeparamref name="TResult"/>, polling if it is a polling step.</summary>
  public static async Task<object?> InvokeAsync<TResult>(StepHandle<TResult> step, TaskOrchestrationContext context, object? input, TaskOptions? options, ILogger logger) =>
    step.Kind switch
    {
      StepKind.Activity => await context.CallActivityAsync<TResult>(step.TargetName, input, options),
      StepKind.SubOrchestration => await context.CallSubOrchestratorAsync<TResult>(step.TargetName, input, options),
      StepKind.Polling => await PollingRunner.RunAsync(step, step.Polling!, context, input, options, logger),
      _ => throw new UnreachableException($"Step '{step.Name}': unknown step kind '{step.Kind}'."),
    };
}
