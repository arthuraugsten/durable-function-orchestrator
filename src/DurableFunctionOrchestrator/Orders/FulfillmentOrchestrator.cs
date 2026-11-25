using DurableFunctionOrchestrator.Sdk;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using static DurableFunctionOrchestrator.Orders.OrderActivityNames;

namespace DurableFunctionOrchestrator.Orders;

/// <summary>
/// Example sub-orchestrator, built on the same base class. <see cref="OrderProcessingOrchestrator"/>
/// runs it as a single step, so pipelines can be composed from smaller pipelines.
/// </summary>
/// <remarks>
/// It also shows a polling step: the carrier produces shipping labels asynchronously, so the
/// pipeline sends the HTTP request, then checks the returned status URL on an interval until the
/// label is ready. Running the polling here, in a sub-orchestration, keeps the parent's history small.
/// </remarks>
public sealed class FulfillmentOrchestrator : ActivityOrchestrator<FulfillmentRequest, FulfillmentResult>
{
  /// <summary>Seconds to wait between label status checks.</summary>
  public const int LabelPollingIntervalSeconds = 2;

  /// <summary>Seconds after the label request at which a label that is still pending fails fulfillment.</summary>
  public const int LabelTimeoutSeconds = 20;

  private static readonly StepHandle<Shipment> NewShipment = Steps.Activity<Shipment>(CreateShipment);

  private static readonly StepHandle<ShippingLabel> Label = Steps.Polling(
    start: Steps.Activity<LabelOperation>(RequestShippingLabel),
    check: Steps.Activity<PollStatus<ShippingLabel>>(CheckShippingLabel),
    intervalSeconds: LabelPollingIntervalSeconds,
    timeoutSeconds: LabelTimeoutSeconds,
    name: "ShippingLabel");

  private static readonly StepHandle Pickup = Steps.Activity(SchedulePickup);

  [Function(nameof(FulfillmentOrchestrator))]
  public Task<FulfillmentResult> Run([OrchestrationTrigger] TaskOrchestrationContext context) => ExecuteAsync(context);

  // Retries transient HTTP errors on each carrier call (start and every check).
  protected override TaskOptions DefaultTaskOptions { get; } = TaskOptions.FromRetryPolicy(
    new RetryPolicy(maxNumberOfAttempts: 3, firstRetryInterval: TimeSpan.FromSeconds(1)));

  protected override void ConfigurePipeline(OrchestrationPipelineBuilder<FulfillmentRequest> pipeline) =>
    pipeline
      .Step(NewShipment)
      .Step(Label, state => new LabelRequest(state.GetResult(NewShipment).ShipmentId, state.Input.OrderId, state.Input.Skus))
      .Step(Pickup, state => state.GetResult(NewShipment), step => step.ContinueOnError());

  protected override FulfillmentResult BuildOutput(OrchestrationState<FulfillmentRequest> state) =>
    new(
      state.GetResult(NewShipment).ShipmentId,
      state.GetResult(Label).TrackingNumber,
      PickupScheduled: !state.Failures.ContainsKey(Pickup));
}
