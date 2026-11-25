namespace DurableFunctionOrchestrator.Sdk;

/// <summary>The state of a long-running operation, as reported by a polling step's status check.</summary>
public enum PollState
{
  /// <summary>The operation is still running: wait, then check again.</summary>
  Pending,

  /// <summary>The operation finished; its result is in <see cref="PollStatus{TResult}.Result"/>.</summary>
  Completed,

  /// <summary>The operation finished unsuccessfully; the reason is in <see cref="PollStatus{TResult}.Error"/>.</summary>
  Failed,
}
