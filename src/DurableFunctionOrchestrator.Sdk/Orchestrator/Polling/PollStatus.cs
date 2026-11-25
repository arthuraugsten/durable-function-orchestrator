namespace DurableFunctionOrchestrator.Sdk;

/// <summary>
/// What the status-check step of a polling step (see <see cref="Steps.Polling"/>) returns. Create it
/// with <see cref="Pending"/>, <see cref="Completed"/> or <see cref="Failed"/>.
/// </summary>
/// <remarks>
/// For an HTTP asynchronous request-reply API, the check typically maps
/// <c>202 Accepted</c> to <see cref="Pending"/> (passing the <c>Retry-After</c> header, if any),
/// <c>200 OK</c> to <see cref="Completed"/>, and a terminal error status to <see cref="Failed"/>.
/// </remarks>
/// <param name="State">Whether the operation is still pending, completed or failed.</param>
/// <param name="Result">The operation result, when <paramref name="State"/> is <see cref="PollState.Completed"/>.</param>
/// <param name="Error">Why the operation failed, when <paramref name="State"/> is <see cref="PollState.Failed"/>.</param>
/// <param name="RetryAfterSeconds">
/// When pending: how long to wait before the next check, overriding the step's polling interval
/// (for example from an HTTP <c>Retry-After</c> header). Only a hint longer than the interval is used,
/// and it is capped at <see cref="Steps.MaxPollingIntervalSeconds"/>. <see langword="null"/> uses the interval.
/// </param>
/// <typeparam name="TResult">The type of the operation result.</typeparam>
public sealed record PollStatus<TResult>(PollState State, TResult? Result = default, string? Error = null, int? RetryAfterSeconds = null)
{
  /// <summary>The operation is still running.</summary>
  /// <param name="retryAfterSeconds">Optional wait before the next check, overriding the polling interval.</param>
  public static PollStatus<TResult> Pending(int? retryAfterSeconds = null) => new(PollState.Pending, RetryAfterSeconds: retryAfterSeconds);

  /// <summary>The operation finished with <paramref name="result"/>, which becomes the step's result.</summary>
  public static PollStatus<TResult> Completed(TResult result) => new(PollState.Completed, result);

  /// <summary>The operation finished unsuccessfully. The polling step fails with <paramref name="error"/>.</summary>
  public static PollStatus<TResult> Failed(string error)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(error);
    return new(PollState.Failed, Error: error);
  }
}
