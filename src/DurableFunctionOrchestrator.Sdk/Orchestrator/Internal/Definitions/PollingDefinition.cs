namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>
/// The configuration of a polling step: data only. Created and validated by <see cref="PollingValidator"/>;
/// run by <see cref="PollingRunner"/>. A record, so polling handles keep comparing by value.
/// </summary>
/// <param name="Start">Starts the operation and returns a reference to it.</param>
/// <param name="Check">Reports the operation's status, given that reference.</param>
/// <param name="Schedule">How long to wait before each check.</param>
/// <param name="TimeoutSeconds">Seconds after the start at which a pending operation times out; null for no timeout.</param>
internal sealed record PollingDefinition(StepHandle Start, StepHandle Check, PollingSchedule Schedule, int? TimeoutSeconds);
