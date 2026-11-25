using System.Text.Json;
using DurableFunctionOrchestrator.Orders;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
  .AddApplicationInsightsTelemetryWorkerService()
  .ConfigureFunctionsApplicationInsights();

// Source-generated JSON for the order pipeline's payloads. Durable Functions (isolated) serializes
// with WorkerOptions.Serializer, which is built from these options, so this covers orchestration and
// activity inputs and outputs, and HTTP request bodies read with ReadFromJsonAsync.
builder.Services.Configure<JsonSerializerOptions>(OrderJsonContext.AddTo);

// HTTP client for the carrier API. The demo carrier API is hosted in this app, so the default is
// the local Functions host; set CarrierApiBaseUrl to point at another host.
builder.Services.AddHttpClient(CarrierActivities.HttpClientName, client =>
  client.BaseAddress = new Uri(builder.Configuration["CarrierApiBaseUrl"] ?? "http://localhost:7071/api/"));

builder.Build().Run();
