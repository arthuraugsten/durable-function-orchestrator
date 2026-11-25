using DurableFunctionOrchestrator.Sdk.Internal;

namespace DurableFunctionOrchestrator.Sdk;

/// <summary>
/// Creates <see cref="StepHandle"/>s. Declare them once, typically as <c>static readonly</c> fields
/// on the orchestrator:
/// <code>
/// private static readonly StepHandle&lt;PaymentReceipt&gt; Payment = Steps.Activity&lt;PaymentReceipt&gt;("ProcessPayment");
/// </code>
/// </summary>
/// <remarks>
/// The step name defaults to the activity or orchestrator name. Pass <c>name</c> to call the same
/// target more than once in a pipeline, because step names must be unique.
/// </remarks>
public static class Steps
{
  /// <summary>A step that calls an activity returning <typeparamref name="TResult"/>.</summary>
  public static StepHandle<TResult> Activity<TResult>(string activityName, string? name = null) =>
    new(name ?? activityName, StepKind.Activity, activityName);

  /// <summary>A step that calls an activity with no result.</summary>
  public static StepHandle Activity(string activityName, string? name = null) =>
    new(name ?? activityName, StepKind.Activity, activityName);

  /// <summary>A step that runs another orchestrator, returning <typeparamref name="TResult"/>, as a sub-orchestration.</summary>
  public static StepHandle<TResult> SubOrchestration<TResult>(string orchestratorName, string? name = null) =>
    new(name ?? orchestratorName, StepKind.SubOrchestration, orchestratorName);

  /// <summary>A step that runs another orchestrator with no result as a sub-orchestration.</summary>
  public static StepHandle SubOrchestration(string orchestratorName, string? name = null) =>
    new(name ?? orchestratorName, StepKind.SubOrchestration, orchestratorName);

  /// <summary>
  /// The most status checks a polling step with a timeout may be configured to make. Every check
  /// adds events to the orchestration history, which is replayed each time the orchestration wakes,
  /// so <see cref="Polling"/> rejects schedules that could exceed this. For long operations, use a
  /// longer interval or a backoff.
  /// </summary>
  public const int MaxPollingChecks = 1_000;

  /// <summary>
  /// The longest wait between two checks of a polling step, in seconds (8 minutes). It caps the
  /// interval, the backoff's maximum interval and <see cref="PollStatus{TResult}.RetryAfterSeconds"/>
  /// hints, so a completed operation is always noticed within 8 minutes. Together with
  /// <see cref="MaxPollingChecks"/>, it limits a polling timeout to 480,000 seconds (about 5.5 days).
  /// </summary>
  public const int MaxPollingIntervalSeconds = 8 * 60;

  /// <summary>
  /// A step that starts a long-running operation and polls it until it completes: the
  /// asynchronous request-reply pattern (for example an HTTP API that answers <c>202 Accepted</c>
  /// with a status URL). Operations can run for hours or days: the orchestration is unloaded
  /// between checks.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The step calls <paramref name="start"/> with the step input. Its result (for example a status
  /// URL or operation ID) is the input of every <paramref name="check"/> call. The step then waits
  /// on a durable timer, calls <paramref name="check"/>, and repeats until the check returns
  /// <see cref="PollStatus{TResult}.Completed"/> (the step result) or
  /// <see cref="PollStatus{TResult}.Failed"/>, or until <paramref name="timeoutSeconds"/> elapses.
  /// </para>
  /// <para>
  /// The first wait is <paramref name="intervalSeconds"/>. After each pending check the interval is
  /// multiplied by <paramref name="backoffCoefficient"/>, up to <paramref name="maxIntervalSeconds"/>.
  /// A <see cref="PollStatus{TResult}.RetryAfterSeconds"/> hint longer than the interval lengthens
  /// the next wait; a shorter one is ignored. No wait is ever longer than
  /// <see cref="MaxPollingIntervalSeconds"/> (8 minutes).
  /// </para>
  /// <para>
  /// <paramref name="start"/> and <paramref name="check"/> can be activities or sub-orchestrations;
  /// do the I/O (such as the HTTP calls) there, never in orchestration code. Each start and check
  /// call should return quickly: the waiting happens in the durable timers. Step options apply to
  /// the polling step as a whole (condition, error handling), and its task options (retries) apply
  /// to each start and check call. A failure is reported with this handle; failures raised by the
  /// polling itself use the <see cref="PollingErrorTypes"/> error types.
  /// </para>
  /// <para>
  /// Every check adds events to the orchestration history, so a schedule that could make more than
  /// <see cref="MaxPollingChecks"/> checks before its timeout is rejected. For operations that need
  /// many checks, also run the polling step in its own sub-orchestration to keep the parent's
  /// history small.
  /// </para>
  /// </remarks>
  /// <param name="start">Starts the operation and returns a reference to it.</param>
  /// <param name="check">Takes that reference and reports the operation's <see cref="PollStatus{TResult}"/>.</param>
  /// <param name="intervalSeconds">Seconds to wait before the first check (1 to <see cref="MaxPollingIntervalSeconds"/>).</param>
  /// <param name="timeoutSeconds">
  /// Seconds after the operation is started at which a still-pending operation fails the step with
  /// <see cref="PollingErrorTypes.TimedOut"/>. <see langword="null"/> polls until the operation
  /// completes or fails, with no check budget: set a timeout for anything that might never finish.
  /// </param>
  /// <param name="backoffCoefficient">
  /// Factor applied to the interval after each pending check (at least 1). The default, 1, keeps a
  /// fixed interval.
  /// </param>
  /// <param name="maxIntervalSeconds">
  /// The longest interval the backoff can reach, at most <see cref="MaxPollingIntervalSeconds"/>.
  /// Required when <paramref name="backoffCoefficient"/> is greater than 1; otherwise it defaults to
  /// <paramref name="intervalSeconds"/>.
  /// </param>
  /// <param name="name">The step name. Defaults to the name of <paramref name="start"/>.</param>
  public static StepHandle<TResult> Polling<TOperation, TResult>(
    StepHandle<TOperation> start,
    StepHandle<PollStatus<TResult>> check,
    int intervalSeconds,
    int? timeoutSeconds = null,
    double backoffCoefficient = 1.0,
    int? maxIntervalSeconds = null,
    string? name = null)
  {
    ArgumentNullException.ThrowIfNull(start);
    return new(name ?? start.Name, PollingValidator.CreateDefinition(start, check, intervalSeconds, timeoutSeconds, backoffCoefficient, maxIntervalSeconds));
  }
}
