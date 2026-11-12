// Local development orchestration. Starts both support backends (.NET and Python) and the demo frontend, and
// watches their health.
// Each runs in fake model mode unless its own configuration selects live mode. Each keeps its own state; they share
// nothing but the contract and the synthetic fixtures.
var builder = DistributedApplication.CreateBuilder(args);

var dotnetApi = builder.AddProject<Projects.Support_Api>("support-api-dotnet")
    .WithHttpHealthCheck("/health")
    // DevUI is served by the API itself, in Development only. Show it as a link on the resource.
    // Its chats use the same instrumented agent, so they appear in Traces as invoke_agent > chat > execute_tool.
    .WithUrlForEndpoint("http", _ => new ResourceUrlAnnotation { Url = "/devui/", DisplayText = "DevUI" });

// The Python backend: Aspire runs `uv sync --locked` for it, then starts Uvicorn on `support_agent.app:app`.
// Its logic stays in Python; Aspire only launches it, passes configuration, and watches /health.
var pythonApi = builder.AddUvicornApp("support-api-python", "../src/python", "support_agent.app:app")
    .WithUv(args: ["sync", "--locked"])
    .WithEnvironment("APP_ENVIRONMENT", "development")
    .WithEnvironment("IDENTITY__DEVELOPMENT_CUSTOMER_ID", "CUST-1001")
    // Telemetry: OTLP over gRPC to the dashboard, with its endpoint, API key header, and certificate. The Python
    // code owns its providers (support_agent/telemetry.py); Aspire only says where to send.
    .WithOtlpExporter(OtlpProtocol.Grpc)
    .WithHttpHealthCheck("/health");

// Python DevUI: development only, its own loopback process over the same agents and guarded services. It reuses
// the API's environment (synced above), so it waits for the API rather than syncing again.
builder.AddPythonModule("support-devui-python", "../src/python", "support_agent.devui")
    .WithUv(install: false)
    .WithHttpEndpoint(env: "PORT")
    .WithEnvironment("APP_ENVIRONMENT", "development")
    .WithEnvironment("IDENTITY__DEVELOPMENT_CUSTOMER_ID", "CUST-1001")
    .WithOtlpExporter(OtlpProtocol.Grpc)
    .WithHttpHealthCheck("/health")
    .WaitFor(pythonApi);

// The demo frontend (Next.js, from house-template-nextjs): its /api proxy forwards to the .NET backend, so the
// browser stays on one origin. Telemetry goes to the dashboard over OTLP/HTTP (the only protocol its exporter
// speaks; the launch profiles set the dashboard's HTTP endpoint), with the trace context passed to the backend.
// AddJavaScriptApp (stable) runs its "dev" script; AddNextJsApp is still marked experimental in Aspire 13.6.
builder.AddJavaScriptApp("web", "../frontend", "dev")
    .WithPnpm(true, ["--frozen-lockfile"])
    .WithHttpEndpoint(env: "PORT")
    // The contract's routes are under /api, so the proxy's /api/v1/... reaches {backend}/api/v1/....
    .WithEnvironment("API_BASE_URL", ReferenceExpression.Create($"{dotnetApi.GetEndpoint("http")}/api"))
    .WithOtlpExporter(OtlpProtocol.HttpProtobuf)
    .WithHttpHealthCheck("/health/live")
    .WaitFor(dotnetApi);

builder.Build().Run();
