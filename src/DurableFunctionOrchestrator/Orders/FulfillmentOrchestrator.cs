using DurableFunctionOrchestrator.Sdk;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using static DurableFunctionOrchestrator.Orders.OrderActivityNames;

namespace DurableFunctionOrchestrator.Orders;

/// <summary>
/// Example sub-orchestrator, built on the same base class. <see cref="OrderProcessingOrchestrator"/>
/// runs it as a single step, so pipelines can be composed from smaller pipelines.
/// </summary>
public sealed class FulfillmentOrchestrator : ActivityOrchestrator<FulfillmentRequest, FulfillmentResult>
{
  private static readonly StepHandle<Shipment> NewShipment = Steps.Activity<Shipment>(CreateShipment);
  private static readonly StepHandle Pickup = Steps.Activity(SchedulePickup);

  [Function(nameof(FulfillmentOrchestrator))]
  public Task<FulfillmentResult> Run([OrchestrationTrigger] TaskOrchestrationContext context) => ExecuteAsync(context);

  protected override void ConfigurePipeline(OrchestrationPipelineBuilder<FulfillmentRequest> pipeline) =>
    pipeline
      .Step(NewShipment)
      .Step(Pickup, state => state.GetResult(NewShipment), step => step.ContinueOnError());

  protected override FulfillmentResult BuildOutput(OrchestrationState<FulfillmentRequest> state)
  {
    Shipment shipment = state.GetResult(NewShipment);
    return new FulfillmentResult(shipment.ShipmentId, shipment.TrackingNumber, PickupScheduled: !state.Failures.ContainsKey(Pickup));
  }
}
