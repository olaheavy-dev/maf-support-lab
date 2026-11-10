// Local development orchestration. Starts the .NET support API and watches its health.
// The API runs in fake model mode unless its own configuration selects live mode.
var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Support_Api>("support-api-dotnet")
    .WithHttpHealthCheck("/health");

builder.Build().Run();
