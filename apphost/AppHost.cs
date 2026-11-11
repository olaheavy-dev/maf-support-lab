// Local development orchestration. Starts both support backends (.NET and Python) and watches their health.
// Each runs in fake model mode unless its own configuration selects live mode. Each keeps its own state; they share
// nothing but the contract and the synthetic fixtures.
var builder = DistributedApplication.CreateBuilder(args);

var dotnetApi = builder.AddProject<Projects.Support_Api>("support-api-dotnet")
    .WithHttpHealthCheck("/health")
    // DevUI is served by the API itself, in Development only (L05). Show it as a link on the resource.
    // Its chats use the same instrumented agent, so they appear in Traces as invoke_agent > chat > execute_tool.
    .WithUrlForEndpoint("http", _ => new ResourceUrlAnnotation { Url = "/devui/", DisplayText = "DevUI" });

// The Python backend (L17): Aspire runs `uv sync --locked` for it, then starts Uvicorn on `support_agent.app:app`.
// Its logic stays in Python; Aspire only launches it, passes configuration, and watches /health.
var pythonApi = builder.AddUvicornApp("support-api-python", "../src/python", "support_agent.app:app")
    .WithUv(args: ["sync", "--locked"])
    .WithEnvironment("APP_ENVIRONMENT", "development")
    .WithEnvironment("IDENTITY__DEVELOPMENT_CUSTOMER_ID", "CUST-1001")
    // Telemetry (L40): OTLP over gRPC to the dashboard, with its endpoint, API key header, and certificate. The Python
    // code owns its providers (support_agent/telemetry.py); Aspire only says where to send.
    .WithOtlpExporter(OtlpProtocol.Grpc)
    .WithHttpHealthCheck("/health");

// Python DevUI (L18): development only, its own loopback process over the same agents and guarded services. It reuses
// the API's environment (synced above), so it waits for the API rather than syncing again.
builder.AddPythonModule("support-devui-python", "../src/python", "support_agent.devui")
    .WithUv(install: false)
    .WithHttpEndpoint(env: "PORT")
    .WithEnvironment("APP_ENVIRONMENT", "development")
    .WithEnvironment("IDENTITY__DEVELOPMENT_CUSTOMER_ID", "CUST-1001")
    .WithOtlpExporter(OtlpProtocol.Grpc)
    .WithHttpHealthCheck("/health")
    .WaitFor(pythonApi);

// Browser probe (L22): a disposable page plus a tiny Node proxy (frontend/probes/proxy, no packages). The browser only
// uses relative URLs; the proxy gets each backend's address from these references (services__<name>__http__0) and
// forwards /api/{alias}/v1/... to it. Backend aliases are fixed in the proxy; the browser never sees an address.
builder.AddNodeApp("browser-probe", "../frontend/probes/proxy", "server.mjs")
    .WithHttpEndpoint(env: "PORT")
    .WithReference(dotnetApi)
    .WithReference(pythonApi)
    .WaitFor(dotnetApi)
    .WaitFor(pythonApi)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
