namespace DurableFunctionOrchestrator.Orders;

public sealed record OrderRequest(
  string OrderId,
  string CustomerEmail,
  IReadOnlyList<OrderLine> Lines,
  string? LoyaltyMemberId = null)
{
  public decimal Subtotal => Lines.Sum(line => line.Quantity * line.UnitPrice);
}

public sealed record OrderLine(string Sku, int Quantity, decimal UnitPrice);

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors);

public sealed record InventoryReservation(string ReservationId);

public sealed record ShippingQuote(decimal Cost);

public sealed record PaymentRequest(string OrderId, decimal Amount);

public sealed record PaymentReceipt(string TransactionId, decimal AmountCharged);

public sealed record ConfirmationRequest(string OrderId, string CustomerEmail, string TransactionId);

public sealed record LoyaltyRequest(string MemberId, decimal AmountSpent);

public sealed record FulfillmentRequest(string OrderId, string ReservationId, IReadOnlyList<string> Skus);

public sealed record Shipment(string ShipmentId);

/// <summary>Body of the carrier's label request.</summary>
public sealed record LabelRequest(string ShipmentId, string OrderId, IReadOnlyList<string> Skus);

/// <summary>The carrier's status URL for a label being produced: the reference the polling step checks.</summary>
public sealed record LabelOperation(string StatusUrl);

public sealed record ShippingLabel(string LabelId, string TrackingNumber);

public sealed record CarrierJob(string JobId, string Status);

public sealed record CarrierError(string Error);

public sealed record FulfillmentResult(string ShipmentId, string TrackingNumber, bool PickupScheduled);

public sealed record OrderResult(
  string OrderId,
  string Status,
  string? ReservationId = null,
  string? TransactionId = null,
  decimal? AmountCharged = null,
  string? TrackingNumber = null,
  int? LoyaltyPointsAwarded = null,
  IReadOnlyList<string>? Notes = null);

public static class OrderStatus
{
  public const string Completed = nameof(Completed);
  public const string Rejected = nameof(Rejected);
  public const string PaymentFailed = nameof(PaymentFailed);
  public const string FulfillmentFailed = nameof(FulfillmentFailed);
}
