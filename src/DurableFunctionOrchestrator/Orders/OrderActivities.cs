using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Orders;

/// <summary>Activity names, shared by the activity functions and the orchestrator pipeline.</summary>
public static class OrderActivityNames
{
  public const string ValidateOrder = nameof(ValidateOrder);
  public const string ReserveInventory = nameof(ReserveInventory);
  public const string CalculateShipping = nameof(CalculateShipping);
  public const string ProcessPayment = nameof(ProcessPayment);
  public const string ReleaseInventory = nameof(ReleaseInventory);
  public const string RefundPayment = nameof(RefundPayment);
  public const string SendConfirmation = nameof(SendConfirmation);
  public const string AwardLoyaltyPoints = nameof(AwardLoyaltyPoints);
  public const string CreateShipment = nameof(CreateShipment);
  public const string SchedulePickup = nameof(SchedulePickup);
  public const string RequestShippingLabel = nameof(RequestShippingLabel);
  public const string CheckShippingLabel = nameof(CheckShippingLabel);
  public const string SyncCarrierRates = nameof(SyncCarrierRates);
}

/// <summary>
/// Simulated activities for the order example. In a real app these would call external systems.
/// </summary>
/// <remarks>
/// Some inputs trigger simulated failures, so every pipeline outcome can be demonstrated:
/// <list type="bullet">
///   <item>An amount over <see cref="PaymentLimit"/> is declined: payment fails, inventory is released (compensation).</item>
///   <item>A SKU starting with <see cref="OutOfStockSkuPrefix"/> can't be reserved: an unhandled failure fails the orchestration.</item>
///   <item>An email ending with <see cref="BouncingEmailDomain"/> can't be sent: the order still completes (ContinueOnError).</item>
/// </list>
/// </remarks>
public static class OrderActivities
{
  public const decimal PaymentLimit = 10_000m;
  public const string OutOfStockSkuPrefix = "OUT-OF-STOCK";
  public const string BouncingEmailDomain = "@bounce.test";

  /// <summary>The longest simulated rate sync, in seconds.</summary>
  public const int MaxRateSyncSeconds = 120;

  [Function(OrderActivityNames.ValidateOrder)]
  public static ValidationResult ValidateOrder([ActivityTrigger] OrderRequest order)
  {
    var errors = new List<string>();

    if (string.IsNullOrWhiteSpace(order.CustomerEmail))
    {
      errors.Add("Customer email is required.");
    }

    if (order.Lines is not { Count: > 0 })
    {
      errors.Add("The order has no lines.");
    }
    else if (order.Lines.Any(line => line.Quantity <= 0 || line.UnitPrice < 0))
    {
      errors.Add("Every line needs a positive quantity and a non-negative price.");
    }

    return new ValidationResult(errors.Count == 0, errors);
  }

  [Function(OrderActivityNames.ReserveInventory)]
  public static InventoryReservation ReserveInventory([ActivityTrigger] OrderRequest order, FunctionContext context)
  {
    // Simulated stock-out, so the example can demonstrate an unhandled failure.
    if (order.Lines.FirstOrDefault(line => line.Sku.StartsWith(OutOfStockSkuPrefix, StringComparison.OrdinalIgnoreCase)) is { } outOfStock)
      throw new InvalidOperationException($"SKU '{outOfStock.Sku}' is out of stock.");

    string reservationId = $"RES-{Guid.NewGuid():N}";
    context.GetLogger(OrderActivityNames.ReserveInventory)
      .LogInformation("Reserved {LineCount} line(s) for order {OrderId} as {ReservationId}.", order.Lines.Count, order.OrderId, reservationId);
    return new InventoryReservation(reservationId);
  }

  [Function(OrderActivityNames.CalculateShipping)]
  public static ShippingQuote CalculateShipping([ActivityTrigger] OrderRequest order) =>
    new(5m + (0.5m * order.Lines.Sum(line => line.Quantity)));

  [Function(OrderActivityNames.ProcessPayment)]
  public static PaymentReceipt ProcessPayment([ActivityTrigger] PaymentRequest payment, FunctionContext context)
  {
    // Simulated decline, so the example can demonstrate failure handling and compensation.
    if (payment.Amount > PaymentLimit)
      throw new InvalidOperationException($"Payment of {payment.Amount:C} declined: exceeds the {PaymentLimit:C} limit.");

    string transactionId = $"TXN-{Guid.NewGuid():N}";
    context.GetLogger(OrderActivityNames.ProcessPayment)
      .LogInformation("Charged {Amount} for order {OrderId} ({TransactionId}).", payment.Amount, payment.OrderId, transactionId);
    return new PaymentReceipt(transactionId, payment.Amount);
  }

  [Function(OrderActivityNames.ReleaseInventory)]
  public static void ReleaseInventory([ActivityTrigger] string reservationId, FunctionContext context) =>
    context.GetLogger(OrderActivityNames.ReleaseInventory)
      .LogInformation("Released inventory reservation {ReservationId}.", reservationId);

  [Function(OrderActivityNames.RefundPayment)]
  public static void RefundPayment([ActivityTrigger] PaymentReceipt receipt, FunctionContext context) =>
    context.GetLogger(OrderActivityNames.RefundPayment)
      .LogInformation("Refunded {Amount} for transaction {TransactionId}.", receipt.AmountCharged, receipt.TransactionId);

  [Function(OrderActivityNames.SendConfirmation)]
  public static void SendConfirmation([ActivityTrigger] ConfirmationRequest request, FunctionContext context)
  {
    // Simulated bounce, so the example can demonstrate a non-critical (ContinueOnError) failure.
    if (request.CustomerEmail.EndsWith(BouncingEmailDomain, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException($"Confirmation email to '{request.CustomerEmail}' bounced.");

    context.GetLogger(OrderActivityNames.SendConfirmation)
      .LogInformation("Sent confirmation for order {OrderId} to {Email}.", request.OrderId, request.CustomerEmail);
  }

  /// <summary>Simulates a slow refresh of carrier rates, so a second run can be started while it works.</summary>
  [Function(OrderActivityNames.SyncCarrierRates)]
  public static async Task<int> SyncCarrierRates([ActivityTrigger] RateSyncRequest request, FunctionContext context, CancellationToken cancellationToken)
  {
    int seconds = Math.Clamp(request.DurationSeconds, 1, MaxRateSyncSeconds);
    await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);

    const int ratesUpdated = 42;
    context.GetLogger(OrderActivityNames.SyncCarrierRates)
      .LogInformation("Refreshed {RateCount} carrier rates in {Seconds}s.", ratesUpdated, seconds);
    return ratesUpdated;
  }

  [Function(OrderActivityNames.AwardLoyaltyPoints)]
  public static int AwardLoyaltyPoints([ActivityTrigger] LoyaltyRequest request) => (int)Math.Floor(request.AmountSpent);

  [Function(OrderActivityNames.CreateShipment)]
  public static Shipment CreateShipment([ActivityTrigger] FulfillmentRequest request) => new($"SHP-{request.OrderId}");

  [Function(OrderActivityNames.SchedulePickup)]
  public static void SchedulePickup([ActivityTrigger] Shipment shipment, FunctionContext context) =>
    context.GetLogger(OrderActivityNames.SchedulePickup)
      .LogInformation("Scheduled carrier pickup for shipment {ShipmentId}.", shipment.ShipmentId);
}
