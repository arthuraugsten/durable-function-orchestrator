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

// HTTP client for the carrier API. The demo carrier API is hosted in this app, so the default is
// the local Functions host; set CarrierApiBaseUrl to point at another host.
builder.Services.AddHttpClient(CarrierActivities.HttpClientName, client =>
  client.BaseAddress = new Uri(builder.Configuration["CarrierApiBaseUrl"] ?? "http://localhost:7071/api/"));

builder.Build().Run();
