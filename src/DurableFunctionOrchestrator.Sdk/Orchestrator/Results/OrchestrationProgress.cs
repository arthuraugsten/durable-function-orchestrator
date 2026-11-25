namespace DurableFunctionOrchestrator.Sdk;

/// <summary>
/// Published as the orchestration custom status, so clients can follow progress through the
/// status query endpoint.
/// </summary>
/// <param name="Status">Running, Completed, Stopped or Failed.</param>
/// <param name="CurrentStage">1-based index of the running stage; null once finished.</param>
/// <param name="TotalStages">Number of stages in the pipeline.</param>
/// <param name="Detail">The running stage, the stop reason, or the failed step.</param>
/// <param name="CompletedSteps">Steps that completed successfully.</param>
/// <param name="SkippedSteps">Steps skipped by their condition.</param>
/// <param name="FailedSteps">Steps that failed but were allowed to continue.</param>
public sealed record OrchestrationProgress(
  string Status,
  int? CurrentStage,
  int TotalStages,
  string? Detail,
  IReadOnlyList<string> CompletedSteps,
  IReadOnlyList<string> SkippedSteps,
  IReadOnlyList<string> FailedSteps);
