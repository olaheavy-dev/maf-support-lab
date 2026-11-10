using System.Text.Json;
using Support.Api.Common.Infrastructure;
using Support.Api.Common.Infrastructure.Observability;
using Support.Api.Modules.Agents;
using Support.Api.Modules.Billing;
using Support.Api.Modules.Conversations;
using Support.Api.Modules.Identity;
using Support.Api.Modules.Operations;
using Support.Api.Modules.Orders;
using Support.Api.Modules.Technical;
using Support.Api.Modules.Triage;

// Composition root only: each module registers its own services and maps its own endpoints.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddSupportTelemetry();

// Contract convention: snake_case JSON (contracts/README.md).
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => FixturePolicy.Load());

builder.Services.AddIdentityModule(builder.Configuration, builder.Environment);
builder.Services.AddOperationsModule();
builder.Services.AddOrdersModule();
builder.Services.AddBillingModule();
builder.Services.AddTechnicalModule();
builder.Services.AddTriageModule();
builder.Services.AddConversationsModule();
builder.AddAgentsModule();
builder.AddTriageDevUi();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapConversationsModule();
app.MapOperationsModule();
app.MapIdentityModule();
app.MapAgentsModule();

app.Run();

/// <summary>Entry point, visible to the integration-test host.</summary>
public partial class Program;
