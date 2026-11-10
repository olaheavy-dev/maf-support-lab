using System.Text.Json;
using Microsoft.Extensions.Options;
using Support.Api.Configuration;
using Support.Api.Identity;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Contract convention: snake_case JSON (contracts/README.md).
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

builder.Services.AddModelProvider(builder.Configuration);
builder.Services.AddDevelopmentIdentity(builder.Configuration, builder.Environment);

var app = builder.Build();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    // Development-only diagnostic: shows which principal the server uses. It ignores every
    // header, query string, and body value, which is exactly what the identity tests check.
    app.MapGet("/dev/whoami", (IPrincipalSource principals) => new { customer_id = principals.Current.CustomerId });
}

var modelMode = app.Services.GetRequiredService<IOptions<ModelProviderOptions>>().Value.Mode;
StartupLog.ModelMode(app.Logger, modelMode);

app.Run();

/// <summary>Entry point, visible to the integration-test host.</summary>
public partial class Program;

internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Support API starting. Model provider mode: {ModelMode}")]
    public static partial void ModelMode(ILogger logger, ModelMode modelMode);
}
