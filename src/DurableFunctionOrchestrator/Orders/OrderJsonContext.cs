using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DurableFunctionOrchestrator.Sdk;

namespace DurableFunctionOrchestrator.Orders;

/// <summary>
/// Source-generated JSON metadata for every type the order pipeline passes through Durable Task:
/// orchestration and activity inputs and outputs, and the SDK's custom status. Serialization then uses
/// code generated at compile time instead of reflection, which is faster to start and allocates less.
/// </summary>
/// <remarks>
/// It carries no options of its own, so the worker's options (case-insensitive property names) still
/// apply and the stored JSON keeps its current shape. See <see cref="AddTo"/>.
/// </remarks>
[JsonSerializable(typeof(OrderRequest))]
[JsonSerializable(typeof(OrderResult))]
[JsonSerializable(typeof(ValidationResult))]
[JsonSerializable(typeof(InventoryReservation))]
[JsonSerializable(typeof(ShippingQuote))]
[JsonSerializable(typeof(PaymentRequest))]
[JsonSerializable(typeof(PaymentReceipt))]
[JsonSerializable(typeof(ConfirmationRequest))]
[JsonSerializable(typeof(LoyaltyRequest))]
[JsonSerializable(typeof(FulfillmentRequest))]
[JsonSerializable(typeof(FulfillmentResult))]
[JsonSerializable(typeof(Shipment))]
[JsonSerializable(typeof(LabelRequest))]
[JsonSerializable(typeof(LabelOperation))]
[JsonSerializable(typeof(ShippingLabel))]
[JsonSerializable(typeof(PollStatus<ShippingLabel>))]
[JsonSerializable(typeof(OrchestrationProgress))]
[JsonSerializable(typeof(RateSyncRequest))]
[JsonSerializable(typeof(RateSyncResult))]
[JsonSerializable(typeof(RunLeaseRequest))]
[JsonSerializable(typeof(RunLeaseResult))]
[JsonSerializable(typeof(RunLeaseRelease))]
[JsonSerializable(typeof(OrchestrationRunRecord))]
internal sealed partial class OrderJsonContext : JsonSerializerContext
{
  /// <summary>
  /// Puts this context first in <paramref name="options"/>' type resolvers. Types it doesn't cover
  /// still fall back to reflection, so adding a model without listing it here degrades only
  /// performance, never correctness.
  /// </summary>
  public static void AddTo(JsonSerializerOptions options)
  {
    ArgumentNullException.ThrowIfNull(options);
    options.TypeInfoResolver = JsonTypeInfoResolver.Combine(Default, options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver());
  }
}
