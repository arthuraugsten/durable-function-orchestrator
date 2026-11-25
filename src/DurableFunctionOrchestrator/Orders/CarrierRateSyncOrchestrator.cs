using DurableFunctionOrchestrator.Sdk;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using static DurableFunctionOrchestrator.Orders.OrderActivityNames;

namespace DurableFunctionOrchestrator.Orders;

/// <summary>
/// Example of a job that must never run twice at the same time, even when started from several
/// regions: it refreshes the carrier's shipping rates. <see cref="RunLease"/> makes the base class take a
/// lease keyed by this orchestrator's name before running, and reject other runs while it is held.
/// </summary>
public sealed class CarrierRateSyncOrchestrator : ActivityOrchestrator<RateSyncRequest, RateSyncResult>
{
  /// <summary>
  /// How long a run holds the lease if it never releases it (for example, if it is terminated). Longer
  /// than the longest sync (<see cref="OrderActivities.MaxRateSyncSeconds"/>), short enough to recover quickly.
  /// </summary>
  public const int LeaseSeconds = 10 * 60;

  private static readonly StepHandle<int> Sync = Steps.Activity<int>(SyncCarrierRates);

  [Function(nameof(CarrierRateSyncOrchestrator))]
  public Task<RateSyncResult> Run([OrchestrationTrigger] TaskOrchestrationContext context) => ExecuteAsync(context);

  protected override RunLeaseOptions RunLease { get; } = new(LeaseSeconds);

  protected override void ConfigurePipeline(OrchestrationPipelineBuilder<RateSyncRequest> pipeline) =>
    pipeline.Step(Sync);

  protected override RateSyncResult BuildOutput(OrchestrationState<RateSyncRequest> state) =>
    new(RateSyncStatus.Completed, RatesUpdated: state.GetResult(Sync));

  // Another run holds the lease: report it as a result instead of failing this orchestration.
  protected override Task<RateSyncResult> OnConcurrentRunRejectedAsync(
    TaskOrchestrationContext context,
    OrchestrationState<RateSyncRequest> state,
    OrchestrationRunRecord current) =>
    Task.FromResult(new RateSyncResult(RateSyncStatus.AlreadyRunning, RunningInstanceId: current.InstanceId, RunningSince: current.StartedAt));
}
