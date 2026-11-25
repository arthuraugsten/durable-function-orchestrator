namespace DurableFunctionOrchestrator.Sdk;

/// <summary>What a pipeline step calls.</summary>
public enum StepKind
{
  /// <summary>The step calls an activity.</summary>
  Activity,

  /// <summary>The step calls another orchestrator as a sub-orchestration.</summary>
  SubOrchestration,

  /// <summary>
  /// The step starts a long-running operation, then checks it on an interval until it completes
  /// (see <see cref="Steps.Polling"/>).
  /// </summary>
  Polling,
}
