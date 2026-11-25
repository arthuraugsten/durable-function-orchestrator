namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>
/// The waits of a polling step: <see cref="IntervalSeconds"/> first, then multiplied by
/// <see cref="BackoffCoefficient"/> after each pending check, up to <see cref="MaxIntervalSeconds"/>.
/// Used both to run the step (<see cref="PollingRunner"/>) and to check its budget when it is declared
/// (<see cref="PollingValidator"/>), so the two always agree.
/// </summary>
internal sealed record PollingSchedule(int IntervalSeconds, double BackoffCoefficient, int MaxIntervalSeconds)
{
  /// <summary>The interval after <paramref name="currentSeconds"/>.</summary>
  public int Next(int currentSeconds) =>
    (int)Math.Min(Math.Ceiling(currentSeconds * BackoffCoefficient), MaxIntervalSeconds);

  /// <summary>
  /// The most checks this schedule can make before <paramref name="timeoutSeconds"/>. Retry-After
  /// hints only lengthen waits, so this is an upper bound. Stops counting once it passes <paramref name="limit"/>.
  /// </summary>
  public int CountMaxChecks(int timeoutSeconds, int limit)
  {
    long elapsed = 0;
    int interval = IntervalSeconds;
    int checks = 0;

    while (elapsed < timeoutSeconds && checks <= limit)
    {
      elapsed += Math.Min(interval, timeoutSeconds - elapsed);
      checks++;
      interval = Next(interval);
    }

    return checks;
  }
}
