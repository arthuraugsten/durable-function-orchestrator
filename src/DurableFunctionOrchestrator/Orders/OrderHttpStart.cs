using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace DurableFunctionOrchestrator.Orders;

public static class OrderHttpStart
{
  /// <summary>POST /api/orders with an <see cref="OrderRequest"/> body to start an order orchestration.</summary>
  [Function(nameof(OrderHttpStart))]
  public static async Task<HttpResponseData> Run(
    [HttpTrigger(AuthorizationLevel.Function, "post", Route = "orders")] HttpRequestData req,
    [DurableClient] DurableTaskClient client,
    FunctionContext executionContext,
    CancellationToken cancellationToken)
  {
    OrderRequest? order = await req.ReadFromJsonAsync<OrderRequest>();
    if (order is null || string.IsNullOrWhiteSpace(order.OrderId))
    {
      HttpResponseData badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
      await badRequest.WriteStringAsync("Request body must be an order with an orderId.");
      return badRequest;
    }

    string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(nameof(OrderProcessingOrchestrator), order, cancellationToken);

    executionContext.GetLogger(nameof(OrderHttpStart))
      .LogInformation("Started order orchestration {InstanceId} for order {OrderId}.", instanceId, order.OrderId);

    return await client.CreateCheckStatusResponseAsync(req, instanceId, cancellationToken);
  }
}
