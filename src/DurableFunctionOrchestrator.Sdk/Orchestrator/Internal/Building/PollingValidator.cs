namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>
/// Validates the arguments of <see cref="Steps.Polling"/> and creates the <see cref="PollingDefinition"/>.
/// Invalid configuration fails when the handle is declared, with a message that says how to fix it.
/// </summary>
internal static class PollingValidator
{
  public static PollingDefinition CreateDefinition(
    StepHandle start,
    StepHandle check,
    int intervalSeconds,
    int? timeoutSeconds,
    double backoffCoefficient,
    int? maxIntervalSeconds)
  {
    ArgumentNullException.ThrowIfNull(start);
    ArgumentNullException.ThrowIfNull(check);
    ArgumentOutOfRangeException.ThrowIfLessThan(intervalSeconds, 1);

    if (intervalSeconds > Steps.MaxPollingIntervalSeconds)
      throw new ArgumentOutOfRangeException(nameof(intervalSeconds), intervalSeconds, $"The polling interval can be at most {Steps.MaxPollingIntervalSeconds} seconds (8 minutes).");

    if (start.Kind == StepKind.Polling || check.Kind == StepKind.Polling)
      throw new ArgumentException("The start and check of a polling step must be activity or sub-orchestration steps, not polling steps.");

    if (timeoutSeconds is int timeout && timeout < intervalSeconds)
      throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), timeout, $"The timeout must be at least the polling interval ({intervalSeconds} second(s)).");

    if (!double.IsFinite(backoffCoefficient) || backoffCoefficient < 1)
      throw new ArgumentOutOfRangeException(nameof(backoffCoefficient), backoffCoefficient, "The backoff coefficient must be a finite number of at least 1 (1 keeps a fixed interval).");

    if (backoffCoefficient > 1 && maxIntervalSeconds is null)
      throw new ArgumentException("Set maxIntervalSeconds when using a backoff, so the interval stops growing (for example 480 for 8 minutes).", nameof(maxIntervalSeconds));

    if (maxIntervalSeconds is int maxInterval && maxInterval < intervalSeconds)
      throw new ArgumentOutOfRangeException(nameof(maxIntervalSeconds), maxInterval, $"The maximum interval must be at least the polling interval ({intervalSeconds} second(s)).");

    if (maxIntervalSeconds > Steps.MaxPollingIntervalSeconds)
      throw new ArgumentOutOfRangeException(nameof(maxIntervalSeconds), maxIntervalSeconds, $"The maximum interval can be at most {Steps.MaxPollingIntervalSeconds} seconds (8 minutes).");

    var schedule = new PollingSchedule(intervalSeconds, backoffCoefficient, maxIntervalSeconds ?? intervalSeconds);

    if (timeoutSeconds is int budgetTimeout && !FitsBudget(schedule, budgetTimeout))
      throw new ArgumentException(BudgetError(intervalSeconds, budgetTimeout), nameof(timeoutSeconds));

    return new PollingDefinition(start, check, schedule, timeoutSeconds);
  }

  private static bool FitsBudget(PollingSchedule schedule, int timeoutSeconds) =>
    schedule.CountMaxChecks(timeoutSeconds, Steps.MaxPollingChecks) <= Steps.MaxPollingChecks;

  // Suggests the fastest fix that fits: a backoff up to the 8-minute cap. When even that can't
  // fit, the timeout itself is too long for the budget.
  private static string BudgetError(int intervalSeconds, int timeoutSeconds)
  {
    string problem =
      $"Polling every {intervalSeconds} second(s) for up to {timeoutSeconds} second(s) could make more than {Steps.MaxPollingChecks} checks, " +
      "and every check adds to the orchestration history.";

    var backoff = new PollingSchedule(intervalSeconds, 2, Steps.MaxPollingIntervalSeconds);
    if (FitsBudget(backoff, timeoutSeconds))
      return $"{problem} Use a longer interval, or a backoff (for example backoffCoefficient: 2, maxIntervalSeconds: {Steps.MaxPollingIntervalSeconds}).";

    return $"{problem} Even checking every {Steps.MaxPollingIntervalSeconds} seconds (the 8-minute maximum), the longest timeout is " +
      $"{Steps.MaxPollingChecks * Steps.MaxPollingIntervalSeconds} seconds (about 5.5 days). Use a shorter timeout.";
  }
}
