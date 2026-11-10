using System.Text.Json;
using Support.Api.Common.Infrastructure.Observability;
using Support.Api.Modules.Agents;
using Support.Api.Modules.Conversations;
using Support.Api.Modules.Identity;
using Support.Api.Modules.Orders;
using Support.Api.Modules.Triage;

// Composition root only: each module registers its own services and maps its own endpoints.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddSupportTelemetry();

// Contract convention: snake_case JSON (contracts/README.md).
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddIdentityModule(builder.Configuration, builder.Environment);
builder.Services.AddOrdersModule();
builder.Services.AddTriageModule();
builder.Services.AddConversationsModule();
builder.AddAgentsModule();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapConversationsModule();
app.MapIdentityModule();
app.MapAgentsModule();

app.Run();

/// <summary>Entry point, visible to the integration-test host.</summary>
public partial class Program;
