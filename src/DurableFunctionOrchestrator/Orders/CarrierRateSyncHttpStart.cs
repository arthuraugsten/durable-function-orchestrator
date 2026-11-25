using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Orders;

public static class CarrierRateSyncHttpStart
{
  /// <summary>
  /// POST /api/jobs/carrier-rate-sync starts a rate sync. The body is optional:
  /// <c>{ "durationSeconds": 20 }</c> sets how long the simulated sync takes.
  /// </summary>
  [Function(nameof(CarrierRateSyncHttpStart))]
  public static async Task<HttpResponseData> Run(
    [HttpTrigger(AuthorizationLevel.Function, "post", Route = "jobs/carrier-rate-sync")] HttpRequestData req,
    [DurableClient] DurableTaskClient client,
    FunctionContext executionContext,
    CancellationToken cancellationToken)
  {
    // The body is optional, so read it as text first: an empty body means the defaults.
    string body = await req.ReadAsStringAsync() ?? "";
    RateSyncRequest request = string.IsNullOrWhiteSpace(body)
      ? new RateSyncRequest()
      : JsonSerializer.Deserialize<RateSyncRequest>(body, JsonSerializerOptions.Web) ?? new RateSyncRequest();

    string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(nameof(CarrierRateSyncOrchestrator), request, cancellationToken);

    executionContext.GetLogger(nameof(CarrierRateSyncHttpStart))
      .LogInformation("Started carrier rate sync {InstanceId}.", instanceId);

    return await client.CreateCheckStatusResponseAsync(req, instanceId, cancellationToken);
  }
}
