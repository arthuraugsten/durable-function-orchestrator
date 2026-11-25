namespace DurableFunctionOrchestrator.Sdk;

/// <summary>The output of the acquire and renew activities.</summary>
/// <param name="Acquired">True if the requesting run holds the lease: it acquired it, or renewed it.</param>
/// <param name="Record">
/// The stored record: the requesting run's own record when <paramref name="Acquired"/> is true, otherwise the
/// record that blocks it. Null only when a renewal finds no record at all.
/// </param>
public sealed record RunLeaseResult(bool Acquired, OrchestrationRunRecord? Record);
