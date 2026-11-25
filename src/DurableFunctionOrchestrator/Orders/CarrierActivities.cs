using System.Net;
using System.Net.Http.Json;
using DurableFunctionOrchestrator.Sdk;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Orders;

/// <summary>
/// Activities that call the carrier's HTTP API. They are the start and check steps of the
/// fulfillment pipeline's polling step: all I/O happens here, never in orchestration code.
/// </summary>
public sealed class CarrierActivities
{
  /// <summary>Name of the <see cref="HttpClient"/> registered for the carrier API in <c>Program.cs</c>.</summary>
  public const string HttpClientName = "Carrier";

  private readonly IHttpClientFactory _httpClientFactory;
  private readonly ILogger<CarrierActivities> _logger;

  public CarrierActivities(IHttpClientFactory httpClientFactory, ILogger<CarrierActivities> logger)
  {
    _httpClientFactory = httpClientFactory;
    _logger = logger;
  }

  /// <summary>Start step: asks the carrier for a label and returns the status URL it answers with.</summary>
  [Function(OrderActivityNames.RequestShippingLabel)]
  public async Task<LabelOperation> RequestShippingLabel([ActivityTrigger] LabelRequest request, CancellationToken cancellationToken)
  {
    HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
    var requestUri = new Uri(client.BaseAddress!, "carrier/labels");
    using HttpResponseMessage response = await client.PostAsJsonAsync(requestUri, request, CarrierJsonContext.Default.LabelRequest, cancellationToken);

    if (response.StatusCode != HttpStatusCode.Accepted)
      throw new HttpRequestException($"Label request for shipment '{request.ShipmentId}' returned {(int)response.StatusCode} ({response.StatusCode}); expected 202 Accepted.");

    Uri location = response.Headers.Location
      ?? throw new HttpRequestException($"Label request for shipment '{request.ShipmentId}' returned 202 Accepted without a Location header.");
    Uri statusUrl = location.IsAbsoluteUri ? location : new Uri(requestUri, location);

    _logger.LogInformation("Requested a label for shipment {ShipmentId}; polling {StatusUrl}.", request.ShipmentId, statusUrl);
    return new LabelOperation(statusUrl.ToString());
  }

  /// <summary>
  /// Check step: maps the status URL's response to a <see cref="PollStatus{TResult}"/>.
  /// 202 is pending, 200 is the finished label, 422 is a rejection. Anything else throws, so the
  /// task retry policy handles transient errors.
  /// </summary>
  [Function(OrderActivityNames.CheckShippingLabel)]
  public async Task<PollStatus<ShippingLabel>> CheckShippingLabel([ActivityTrigger] LabelOperation operation, CancellationToken cancellationToken)
  {
    HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
    using HttpResponseMessage response = await client.GetAsync(operation.StatusUrl, cancellationToken);

    switch (response.StatusCode)
    {
      case HttpStatusCode.Accepted:
        // Honour the server's Retry-After, if it sends one; otherwise the step's interval applies.
        int? retryAfterSeconds = response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : null;
        _logger.LogInformation("Label at {StatusUrl} is still being produced.", operation.StatusUrl);
        return PollStatus<ShippingLabel>.Pending(retryAfterSeconds);

      case HttpStatusCode.OK:
        ShippingLabel label = await response.Content.ReadFromJsonAsync(CarrierJsonContext.Default.ShippingLabel, cancellationToken)
          ?? throw new HttpRequestException($"Label at {operation.StatusUrl} returned 200 OK without a body.");
        _logger.LogInformation("Label {LabelId} is ready (tracking {TrackingNumber}).", label.LabelId, label.TrackingNumber);
        return PollStatus<ShippingLabel>.Completed(label);

      case HttpStatusCode.UnprocessableEntity:
        CarrierError? error = await response.Content.ReadFromJsonAsync(CarrierJsonContext.Default.CarrierError, cancellationToken);
        return PollStatus<ShippingLabel>.Failed(error?.Error ?? "The carrier rejected the label.");

      default:
        throw new HttpRequestException($"Label status at {operation.StatusUrl} returned {(int)response.StatusCode} ({response.StatusCode}).");
    }
  }
}
