using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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
/// <c>Location</c> is a <b>relative</b> URL, resolved by the client against the URL it called. Behind a reverse proxy
/// or API gateway the status URL then keeps the public host, scheme and any path prefix; an absolute URL built from
/// the request the host received would point at the internal address instead.
/// </remarks>
/// <remarks>
/// The API is stateless: each job ID encodes when the label is ready, whether it will be rejected, and the shipment.
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
    LabelRequest? request = await JsonSerializer.DeserializeAsync(req.Body, CarrierJsonContext.Default.LabelRequest, context.CancellationToken);
    if (request is null || string.IsNullOrWhiteSpace(request.ShipmentId))
      return await JsonAsync(req, HttpStatusCode.BadRequest, new CarrierError("A shipmentId is required."), CarrierJsonContext.Default.CarrierError);

    bool rejected = request.Skus.Any(sku => sku.StartsWith(OversizedSkuPrefix, StringComparison.OrdinalIgnoreCase));
    bool slow = request.Skus.Any(sku => sku.StartsWith(SlowCarrierSkuPrefix, StringComparison.OrdinalIgnoreCase));
    long readyAt = DateTimeOffset.UtcNow.AddSeconds(slow ? SlowLabelProcessingSeconds : LabelProcessingSeconds).ToUnixTimeSeconds();
    string jobId = $"{(rejected ? Rejected : Accepted)}.{readyAt}.{request.ShipmentId}";

    context.GetLogger(nameof(SimulatedCarrierApi))
      .LogInformation("Carrier accepted label job {JobId} for shipment {ShipmentId}.", jobId, request.ShipmentId);

    // Relative to POST .../carrier/labels, "labels/{jobId}" resolves to .../carrier/labels/{jobId}.
    return await PendingAsync(req, jobId, location: $"labels/{Uri.EscapeDataString(jobId)}");
  }

  /// <summary>GET /api/carrier/labels/{jobId}: reports whether the label is ready.</summary>
  [Function(nameof(CarrierGetLabel))]
  public static async Task<HttpResponseData> CarrierGetLabel(
    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "carrier/labels/{jobId}")] HttpRequestData req,
    string jobId)
  {
    string[] parts = jobId.Split('.', 3);
    if (parts.Length != 3 || !long.TryParse(parts[1], out long readyAt))
      return await JsonAsync(req, HttpStatusCode.NotFound, new CarrierError($"Unknown label job '{jobId}'."), CarrierJsonContext.Default.CarrierError);

    // Relative to GET .../carrier/labels/{jobId}, "{jobId}" resolves to the same status URL.
    if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < readyAt)
      return await PendingAsync(req, jobId, location: Uri.EscapeDataString(jobId));

    string shipmentId = parts[2];
    return parts[0] == Rejected
      ? await JsonAsync(req, HttpStatusCode.UnprocessableEntity, new CarrierError($"Carrier rejected shipment '{shipmentId}': an item exceeds the size limit."), CarrierJsonContext.Default.CarrierError)
      : await JsonAsync(req, HttpStatusCode.OK, new ShippingLabel($"LBL-{shipmentId}", TrackingNumber(jobId)), CarrierJsonContext.Default.ShippingLabel);
  }

  /// <summary>
  /// A tracking number unique to the label job (which includes the shipment), and the same on every poll of it:
  /// "TRK" and 12 hex digits of the job ID's SHA-256.
  /// </summary>
  public static string TrackingNumber(string jobId) =>
    "TRK" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jobId)), 0, 6);

  private static Task<HttpResponseData> PendingAsync(HttpRequestData req, string jobId, string location) =>
    JsonAsync(
      req,
      HttpStatusCode.Accepted,
      new CarrierJob(jobId, "Running"),
      CarrierJsonContext.Default.CarrierJob,
      location: new Uri(location, UriKind.Relative));

  // Serializes straight into the response body with generated metadata: no intermediate string.
  // Every header is set before the body is written: with ASP.NET Core integration, writing the body sends the
  // response, so a header added afterwards is silently dropped.
  private static async Task<HttpResponseData> JsonAsync<T>(HttpRequestData req, HttpStatusCode statusCode, T body, JsonTypeInfo<T> typeInfo, Uri? location = null)
  {
    HttpResponseData response = req.CreateResponse(statusCode);
    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
    if (location is not null)
      response.Headers.Add("Location", location.ToString());

    await JsonSerializer.SerializeAsync(response.Body, body, typeInfo);
    return response;
  }
}
