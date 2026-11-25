using System.Text.Json;
using System.Text.Json.Serialization;

namespace DurableFunctionOrchestrator.Orders;

/// <summary>
/// Source-generated JSON metadata for the carrier HTTP API, shared by <see cref="SimulatedCarrierApi"/>
/// (the server) and <see cref="CarrierActivities"/> (the client), so both always agree on the wire format:
/// web defaults (camelCase, case-insensitive).
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(LabelRequest))]
[JsonSerializable(typeof(ShippingLabel))]
[JsonSerializable(typeof(CarrierJob))]
[JsonSerializable(typeof(CarrierError))]
internal sealed partial class CarrierJsonContext : JsonSerializerContext;
