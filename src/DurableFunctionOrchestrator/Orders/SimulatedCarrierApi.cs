using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Orders;

/// <summary>
/// A stand-in for an external carrier's HTTP API, hosted in this app so the demo runs locally. Label
/// creation follows the asynchronous request-reply pattern: <c>POST carrier/labels</c> answers
/// <c>202 Accepted</c> with a <c>Location</c> status URL, and that URL answers <c>202</c> while the
/// label is being produced, then <c>200</c> with the label or <c>422</c> if the carrier rejects it.
/// </summary>
/// <remarks>
/// The API is stateless: each job ID encodes when the label is ready and whether it will be rejected.
/// Some SKUs trigger simulated outcomes, so every polling outcome can be demonstrated:
/// <list type="bullet">
///   <item>A SKU starting with <see cref="OversizedSkuPrefix"/>: the carrier rejects the label (the operation fails).</item>
///   <item>A SKU starting with <see cref="SlowCarrierSkuPrefix"/>: the label takes an hour, so polling times out.</item>
/// </list>
/// </remarks>
public static class SimulatedCarrierApi
{
  public const int LabelProcessingSeconds = 5;
  public const string OversizedSkuPrefix = "OVERSIZED";
  public const string SlowCarrierSkuPrefix = "SLOW-SHIP";

  private const int SlowLabelProcessingSeconds = 3600;
  private const string Accepted = "accepted";
  private const string Rejected = "rejected";

  /// <summary>POST /api/carrier/labels: starts producing a label and returns its status URL.</summary>
  [Function(nameof(CarrierCreateLabel))]
  public static async Task<HttpResponseData> CarrierCreateLabel(
    [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "carrier/labels")] HttpRequestData req,
    FunctionContext context)
  {
    LabelRequest? request = await req.ReadFromJsonAsync<LabelRequest>();
    if (request is null || string.IsNullOrWhiteSpace(request.ShipmentId))
      return await JsonAsync(req, HttpStatusCode.BadRequest, new CarrierError("A shipmentId is required."));

    bool rejected = request.Skus.Any(sku => sku.StartsWith(OversizedSkuPrefix, StringComparison.OrdinalIgnoreCase));
    bool slow = request.Skus.Any(sku => sku.StartsWith(SlowCarrierSkuPrefix, StringComparison.OrdinalIgnoreCase));
    long readyAt = DateTimeOffset.UtcNow.AddSeconds(slow ? SlowLabelProcessingSeconds : LabelProcessingSeconds).ToUnixTimeSeconds();
    string jobId = $"{(rejected ? Rejected : Accepted)}.{readyAt}.{request.ShipmentId}";

    context.GetLogger(nameof(SimulatedCarrierApi))
      .LogInformation("Carrier accepted label job {JobId} for shipment {ShipmentId}.", jobId, request.ShipmentId);

    return await PendingAsync(req, jobId);
  }

  /// <summary>GET /api/carrier/labels/{jobId}: reports whether the label is ready.</summary>
  [Function(nameof(CarrierGetLabel))]
  public static async Task<HttpResponseData> CarrierGetLabel(
    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "carrier/labels/{jobId}")] HttpRequestData req,
    string jobId)
  {
    string[] parts = jobId.Split('.', 3);
    if (parts.Length != 3 || !long.TryParse(parts[1], out long readyAt))
      return await JsonAsync(req, HttpStatusCode.NotFound, new CarrierError($"Unknown label job '{jobId}'."));

    if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < readyAt)
      return await PendingAsync(req, jobId);

    string shipmentId = parts[2];
    return parts[0] == Rejected
      ? await JsonAsync(req, HttpStatusCode.UnprocessableEntity, new CarrierError($"Carrier rejected shipment '{shipmentId}': an item exceeds the size limit."))
      : await JsonAsync(req, HttpStatusCode.OK, new ShippingLabel($"LBL-{shipmentId}", $"TRK{readyAt}"));
  }

  private static async Task<HttpResponseData> PendingAsync(HttpRequestData req, string jobId)
  {
    HttpResponseData response = await JsonAsync(req, HttpStatusCode.Accepted, new CarrierJob(jobId, "Running"));
    response.Headers.Add("Location", new Uri(req.Url, $"/api/carrier/labels/{Uri.EscapeDataString(jobId)}").ToString());
    return response;
  }

  private static async Task<HttpResponseData> JsonAsync<T>(HttpRequestData req, HttpStatusCode statusCode, T body)
  {
    HttpResponseData response = req.CreateResponse(statusCode);
    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
    await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonSerializerOptions.Web));
    return response;
  }
}
