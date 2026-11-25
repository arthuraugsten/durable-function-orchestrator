using DurableFunctionOrchestrator.Sdk;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using static DurableFunctionOrchestrator.Orders.OrderActivityNames;

namespace DurableFunctionOrchestrator.Orders;

/// <summary>
/// Example concrete orchestrator. It only declares which steps run and how to shape the
/// result; sequencing, fan-out, retries, progress and failure routing come from the base class.
/// </summary>
public sealed class OrderProcessingOrchestrator : ActivityOrchestrator<OrderRequest, OrderResult>
{
  // Typed step handles: declared once, used to build the pipeline and to read results and failures.
  private static readonly StepHandle<ValidationResult> Validation = Steps.Activity<ValidationResult>(ValidateOrder);
  private static readonly StepHandle<InventoryReservation> Reservation = Steps.Activity<InventoryReservation>(ReserveInventory);
  private static readonly StepHandle<ShippingQuote> Shipping = Steps.Activity<ShippingQuote>(CalculateShipping);
  private static readonly StepHandle<PaymentReceipt> Payment = Steps.Activity<PaymentReceipt>(ProcessPayment);
  private static readonly StepHandle<FulfillmentResult> Fulfillment = Steps.SubOrchestration<FulfillmentResult>(nameof(FulfillmentOrchestrator));
  private static readonly StepHandle Confirmation = Steps.Activity(SendConfirmation);
  private static readonly StepHandle<int> Loyalty = Steps.Activity<int>(AwardLoyaltyPoints);

  // The only Functions-specific code: expose the orchestrator as an orchestration trigger.
  [Function(nameof(OrderProcessingOrchestrator))]
  public Task<OrderResult> Run([OrchestrationTrigger] TaskOrchestrationContext context) => ExecuteAsync(context);

  // Applied to every step that does not set its own options.
  protected override TaskOptions DefaultTaskOptions { get; } = TaskOptions.FromRetryPolicy(
    new RetryPolicy(maxNumberOfAttempts: 3, firstRetryInterval: TimeSpan.FromSeconds(2), backoffCoefficient: 2.0));

  protected override void ConfigurePipeline(OrchestrationPipelineBuilder<OrderRequest> pipeline) =>
    pipeline
      // 1. Validate (input defaults to the orchestration input).
      .Step(Validation)

      // 2. Stop early, without failing the orchestration, if validation failed.
      .StopIf(state => !state.GetResult(Validation).IsValid, "Order failed validation.")

      // 3. Fan out: reserve stock and quote shipping concurrently.
      .Parallel(parallel => parallel
        .Step(Reservation)
        .Step(Shipping))

      // 4. Charge the customer, using results from earlier steps.
      .Step(Payment, state => new PaymentRequest(state.Input.OrderId, state.Input.Subtotal + state.GetResult(Shipping).Cost))

      // 5. Ship it: a whole pipeline of its own, run as a sub-orchestration. It polls the carrier's
      //    HTTP API for a shipping label. It is not retried as a whole: its own steps retry, and
      //    re-running it would create a second shipment.
      .Step(
        Fulfillment,
        state => new FulfillmentRequest(state.Input.OrderId, state.GetResult(Reservation).ReservationId, [.. state.Input.Lines.Select(line => line.Sku)]),
        step => step.WithOptions(new TaskOptions(retry: null)))

      // 6. Non-critical follow-ups: run concurrently, and never fail the order.
      .Parallel(parallel => parallel
        .Step(
          Confirmation,
          state => new ConfirmationRequest(state.Input.OrderId, state.Input.CustomerEmail, state.GetResult(Payment).TransactionId),
          step => step.ContinueOnError())
        .Step(
          Loyalty,
          state => new LoyaltyRequest(state.Input.LoyaltyMemberId!, state.GetResult(Payment).AmountCharged),
          step => step
            .When(state => state.Input.LoyaltyMemberId is not null)
            .ContinueOnError()));

  protected override OrderResult BuildOutput(OrchestrationState<OrderRequest> state)
  {
    if (state.IsStopped)
      return new OrderResult(state.Input.OrderId, OrderStatus.Rejected, Notes: state.GetResult(Validation).Errors);

    PaymentReceipt receipt = state.GetResult(Payment);

    return new OrderResult(
      state.Input.OrderId,
      OrderStatus.Completed,
      ReservationId: state.GetResult(Reservation).ReservationId,
      TransactionId: receipt.TransactionId,
      AmountCharged: receipt.AmountCharged,
      TrackingNumber: state.GetResult(Fulfillment).TrackingNumber,
      LoyaltyPointsAwarded: state.TryGetResult(Loyalty, out int points) ? points : null,
      Notes: [.. state.Failures.Select(f => $"{f.Key.Name} failed: {f.Value.ErrorMessage}")]);
  }

  // Compensation (saga): if payment or fulfillment fails, undo the work already done, newest first
  // (refund the payment, release the stock), and return a business result instead of failing the
  // orchestration. Other failures use the default (rethrow).
  protected override async Task<OrderResult> OnStepFailedAsync(
    TaskOrchestrationContext context,
    OrchestrationState<OrderRequest> state,
    StepFailedException failure)
  {
    if (failure.Step != Payment && failure.Step != Fulfillment)
      return await base.OnStepFailedAsync(context, state, failure);

    if (state.TryGetResult(Payment, out PaymentReceipt? receipt))
      await context.CallActivityAsync(RefundPayment, receipt);

    if (state.TryGetResult(Reservation, out InventoryReservation? reservation))
      await context.CallActivityAsync(ReleaseInventory, reservation.ReservationId);

    string status = failure.Step == Payment ? OrderStatus.PaymentFailed : OrderStatus.FulfillmentFailed;
    return new OrderResult(state.Input.OrderId, status, Notes: [failure.FailureDetails.ErrorMessage]);
  }
}
