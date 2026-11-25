using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Sdk.Internal;

/// <summary>
/// The SDK's log messages, generated at compile time by <see cref="LoggerMessageAttribute"/>.
/// </summary>
/// <remarks>
/// Orchestrator code replays on every wake-up, so these calls run many times per orchestration,
/// mostly while replaying, when the replay-safe logger discards them. Generated methods avoid the
/// argument array and the boxing of value types that <c>LogInformation(...)</c> allocates on every call.
/// </remarks>
internal static partial class SdkLog
{
  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Orchestration {InstanceId} starting pipeline with {StageCount} stage(s).")]
  public static partial void PipelineStarting(ILogger logger, string instanceId, int stageCount);

  [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Orchestration {InstanceId} finished with status {Status}.")]
  public static partial void PipelineFinished(ILogger logger, string instanceId, OrchestrationRunStatus status);

  [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Orchestration {InstanceId} failed at step '{StepName}'.")]
  public static partial void PipelineFailed(ILogger logger, Exception exception, string instanceId, string stepName);

  [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Stopping pipeline: {Reason}")]
  public static partial void PipelineStopping(ILogger logger, string reason);

  [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Skipping step '{StepName}': condition not met.")]
  public static partial void StepSkipped(ILogger logger, string stepName);

  [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Running step '{StepName}' ({Kind} '{TargetName}').")]
  public static partial void StepRunning(ILogger logger, string stepName, StepKind kind, string targetName);

  [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Step '{StepName}' failed and is marked ContinueOnError: {Error}")]
  public static partial void StepFailedAndContinued(ILogger logger, string stepName, string error);

  [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Polling step '{StepName}' completed after {CheckCount} check(s).")]
  public static partial void PollingCompleted(ILogger logger, string stepName, int checkCount);

  [LoggerMessage(EventId = 9, Level = LogLevel.Information, Message = "Polling step '{StepName}': still pending after check {CheckCount}; next check in {WaitSeconds}s.")]
  public static partial void PollingPending(ILogger logger, string stepName, int checkCount, int waitSeconds);

  [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "Orchestrator '{OrchestratorName}': instance {InstanceId} acquired the run lease (until {LeaseExpiresAt:O}).")]
  public static partial void RunLeaseAcquired(ILogger logger, string orchestratorName, string instanceId, DateTimeOffset leaseExpiresAt);

  [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "Orchestrator '{OrchestratorName}': instance {InstanceId} was rejected because instance {HolderInstanceId} holds the run lease.")]
  public static partial void RunLeaseRejected(ILogger logger, string orchestratorName, string instanceId, string holderInstanceId);

  [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Orchestrator '{OrchestratorName}': instance {InstanceId} released the run lease as {Status}.")]
  public static partial void RunLeaseReleased(ILogger logger, string orchestratorName, string instanceId, RunStatus status);

  [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "Orchestrator '{OrchestratorName}': instance {InstanceId} could not release the run lease, which will expire on its own: {Error}")]
  public static partial void RunLeaseReleaseFailed(ILogger logger, string orchestratorName, string instanceId, string error);

  [LoggerMessage(EventId = 14, Level = LogLevel.Debug, Message = "Orchestrator '{OrchestratorName}': instance {InstanceId} renewed the run lease.")]
  public static partial void RunLeaseRenewed(ILogger logger, string orchestratorName, string instanceId);

  [LoggerMessage(EventId = 15, Level = LogLevel.Warning, Message = "Orchestrator '{OrchestratorName}': instance {InstanceId} could not renew the run lease: {Error}")]
  public static partial void RunLeaseRenewalFailed(ILogger logger, string orchestratorName, string instanceId, string error);

  [LoggerMessage(EventId = 16, Level = LogLevel.Error, Message = "Orchestrator '{OrchestratorName}': instance {InstanceId} lost the run lease to instance {HolderInstanceId} and stops.")]
  public static partial void RunLeaseLost(ILogger logger, string orchestratorName, string instanceId, string? holderInstanceId);
}
