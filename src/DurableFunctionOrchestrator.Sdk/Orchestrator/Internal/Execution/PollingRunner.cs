using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>Runs a polling step: start the operation, then wait and check it until it completes, fails or times out.</summary>
internal static class PollingRunner
{
  /// <remarks>
  /// Waits use durable timers and the deadline uses <see cref="TaskOrchestrationContext.CurrentUtcDateTime"/>,
  /// so the loop replays deterministically.
  /// </remarks>
  public static async Task<TResult> RunAsync<TResult>(
    StepHandle<TResult> step,
    PollingDefinition polling,
    TaskOrchestrationContext context,
    object? input,
    TaskOptions? options,
    ILogger logger)
  {
    object? operation = await polling.Start.InvokeAsync(context, input, options, logger);

    // The timeout counts from when the operation has been started.
    DateTime? deadline = polling.TimeoutSeconds is { } timeout ? context.CurrentUtcDateTime.AddSeconds(timeout) : null;
    int intervalSeconds = polling.Schedule.IntervalSeconds;
    int waitSeconds = intervalSeconds;

    for (int check = 1; ; check++)
    {
      DateTime fireAt = context.CurrentUtcDateTime.AddSeconds(waitSeconds);
      if (deadline is { } limit && fireAt > limit)
        fireAt = limit;

      await context.CreateTimer(fireAt, CancellationToken.None);

      var status = (PollStatus<TResult>?)await polling.Check.InvokeAsync(context, operation, options, logger)
        ?? throw new InvalidOperationException($"Polling step '{step.Name}': the status check '{polling.Check.Name}' returned null. Return a PollStatus<{typeof(TResult).Name}>.");

      switch (status.State)
      {
        case PollState.Completed:
          SdkLog.PollingCompleted(logger, step.Name, check);
          return status.Result!;

        case PollState.Failed:
          throw Failure(step, PollingErrorTypes.OperationFailed, status.Error ?? "The operation failed.");

        case PollState.Pending when context.CurrentUtcDateTime >= deadline:
          throw Failure(step, PollingErrorTypes.TimedOut, $"The operation was still pending after {polling.TimeoutSeconds} second(s) ({check} check(s)).");

        case PollState.Pending:
          // Back off, then honour a Retry-After hint as a minimum: it can lengthen the next wait
          // but never shorten it, which keeps the number of checks within the declared budget.
          // No wait exceeds the 8-minute cap, so completion is always noticed within 8 minutes.
          intervalSeconds = polling.Schedule.Next(intervalSeconds);
          waitSeconds = status.RetryAfterSeconds is int retryAfter && retryAfter > intervalSeconds
            ? Math.Min(retryAfter, Steps.MaxPollingIntervalSeconds)
            : intervalSeconds;
          SdkLog.PollingPending(logger, step.Name, check, waitSeconds);
          break;

        default:
          throw new InvalidOperationException($"Polling step '{step.Name}': unknown poll state '{status.State}'.");
      }
    }
  }

  // Raised as a task failure so polling failures flow through ContinueOnError and
  // OnStepFailedAsync exactly like a failed activity.
  private static TaskFailedException Failure(StepHandle step, string errorType, string message) =>
    new(step.TargetName, -1, new TaskFailureDetails(errorType, message, null, null, null));
}
