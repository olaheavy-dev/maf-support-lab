// Local development orchestration. Starts the .NET support API and watches its health.
// The API runs in fake model mode unless its own configuration selects live mode.
var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Support_Api>("support-api-dotnet")
    .WithHttpHealthCheck("/health")
    // DevUI is served by the API itself, in Development only (L05). Show it as a link on the resource.
    // Its chats use the same instrumented agent, so they appear in Traces as invoke_agent > chat > execute_tool.
    .WithUrlForEndpoint("http", _ => new ResourceUrlAnnotation { Url = "/devui/", DisplayText = "DevUI" });

builder.Build().Run();
