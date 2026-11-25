namespace DurableFunctionOrchestrator.Sdk;

/// <summary>What a pipeline step calls.</summary>
public enum StepKind
{
  /// <summary>The step calls an activity.</summary>
  Activity,

  /// <summary>The step calls another orchestrator as a sub-orchestration.</summary>
  SubOrchestration,
}
