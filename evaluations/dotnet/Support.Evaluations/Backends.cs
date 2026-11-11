using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Support.Evaluations;

/// <summary>Where a case's requests go: one base URL per customer, and (if the runner owns the processes) a restart.</summary>
internal interface IBackendSession : IAsyncDisposable
{
    IReadOnlyDictionary<string, Uri> Principals { get; }

    /// <summary>Stops every process and starts it again on the same store, as a crash and restart would.</summary>
    Task RestartAsync(CancellationToken cancellationToken);
}

/// <summary>Gives each case the backend it needs: a shared one for read-only cases, a fresh store for write cases.</summary>
internal interface IBackends : IAsyncDisposable
{
    /// <summary>Whether this provider gives each write case its own fresh store (and so may run writes).</summary>
    bool IsolatesWrites { get; }

    /// <summary>Whether a case's session can be restarted (only when the runner owns the processes).</summary>
    bool CanRestart { get; }

    Task<IBackendSession> SessionForAsync(EvaluationCase item, CancellationToken cancellationToken);
}

/// <summary>Backends someone else started (endpoint mode): fixed URLs, no restart, and writes only if promised isolated.</summary>
internal sealed class FixedBackends(IReadOnlyDictionary<string, Uri> principals, bool allowWrites) : IBackends, IBackendSession
{
    public bool IsolatesWrites => allowWrites;

    public bool CanRestart => false;

    public IReadOnlyDictionary<string, Uri> Principals => principals;

    public Task<IBackendSession> SessionForAsync(EvaluationCase item, CancellationToken cancellationToken) => Task.FromResult<IBackendSession>(new Borrowed(this));

    public Task RestartAsync(CancellationToken cancellationToken) => throw new NotSupportedException("Backends started elsewhere can't be restarted by the runner.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>A case borrows the shared backends; ending the case must not stop them.</summary>
    private sealed class Borrowed(FixedBackends owner) : IBackendSession
    {
        public IReadOnlyDictionary<string, Uri> Principals => owner.Principals;

        public Task RestartAsync(CancellationToken cancellationToken) => owner.RestartAsync(cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Backends the runner starts itself (launch mode, L16): each customer gets its own process (a backend serves one
/// development customer per process), all on one SQLite file. Read-only cases share one store; every write case gets a
/// fresh store and fresh processes, so writes never repeat on shared state. Fake model mode only; no credentials.
/// </summary>
internal sealed class LaunchedBackends(string backend, string root, string workDirectory) : IBackends
{
    public static readonly string[] Customers = ["CUST-1001", "CUST-1002"];

    private ProcessSession? shared;
    private int stores;

    public bool IsolatesWrites => true;

    public bool CanRestart => true;

    public async Task<IBackendSession> SessionForAsync(EvaluationCase item, CancellationToken cancellationToken)
    {
        if (item.Reset == "fresh_store")
        {
            return await ProcessSession.StartAsync(backend, root, NewStore(), cancellationToken).ConfigureAwait(false);
        }

        shared ??= await ProcessSession.StartAsync(backend, root, NewStore(), cancellationToken).ConfigureAwait(false);
        return new Shared(shared);
    }

    public async ValueTask DisposeAsync()
    {
        if (shared is not null)
        {
            await shared.DisposeAsync().ConfigureAwait(false);
        }
    }

    private string NewStore()
    {
        var directory = Path.Combine(workDirectory, $"{backend}-{++stores}");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "support.db");
    }

    /// <summary>A read-only case uses the shared processes without owning them.</summary>
    private sealed class Shared(ProcessSession session) : IBackendSession
    {
        public IReadOnlyDictionary<string, Uri> Principals => session.Principals;

        public Task RestartAsync(CancellationToken cancellationToken) => session.RestartAsync(cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>One backend's processes (one per customer) on one store.</summary>
internal sealed class ProcessSession : IBackendSession
{
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(120);
    private static readonly string[] Blocked = ["OTEL_", "ANTHROPIC_", "MODELPROVIDER__", "MODEL_PROVIDER__", "IDENTITY__", "STORAGE__", "ASPNETCORE_", "RESILIENCE__"];
    private readonly string backend;
    private readonly string root;
    private readonly string store;
    private readonly List<Process> processes = [];
    private Dictionary<string, Uri> principals = [];

    private ProcessSession(string backend, string root, string store)
    {
        this.backend = backend;
        this.root = root;
        this.store = store;
    }

    public IReadOnlyDictionary<string, Uri> Principals => principals;

    public static async Task<ProcessSession> StartAsync(string backend, string root, string store, CancellationToken cancellationToken)
    {
        var session = new ProcessSession(backend, root, store);
        try
        {
            await session.StartProcessesAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        StopProcesses();
        await StartProcessesAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        StopProcesses();
        return ValueTask.CompletedTask;
    }

    private async Task StartProcessesAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, Uri> started = [];
        foreach (var customer in LaunchedBackends.Customers)
        {
            var port = FreePort();
            var process = Process.Start(StartInfo(customer, port)) ?? throw new InvalidOperationException($"Could not start the {backend} backend.");
            processes.Add(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var url = new Uri($"http://127.0.0.1:{port}/");
            await WaitHealthyAsync(process, url, cancellationToken).ConfigureAwait(false);
            started[customer] = url;
        }

        principals = started;
    }

    private ProcessStartInfo StartInfo(string customer, int port)
    {
        var info = backend == "dotnet"
            ? new ProcessStartInfo("dotnet") { WorkingDirectory = root, ArgumentList = { "run", "--project", Path.Combine(root, "src", "dotnet", "Support.Api"), "-c", "Release", "--no-build", "--no-launch-profile" } }
            : new ProcessStartInfo("uv") { WorkingDirectory = Path.Combine(root, "src", "python"), ArgumentList = { "run", "--locked", "uvicorn", "support_agent.app:app", "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        // Hermetic: nothing about telemetry, providers, identity, or limits leaks in from the developer's shell.
        foreach (var name in info.Environment.Keys.Where(name => Blocked.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            info.Environment.Remove(name);
        }

        if (backend == "dotnet")
        {
            info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            info.Environment["Identity__DevelopmentCustomerId"] = customer;
            info.Environment["Storage__Path"] = store;
            info.Environment["ModelProvider__Mode"] = "Fake"; // overrides local user-secrets that select Live
        }
        else
        {
            info.Environment["APP_ENVIRONMENT"] = "development";
            info.Environment["IDENTITY__DEVELOPMENT_CUSTOMER_ID"] = customer;
            info.Environment["STORAGE__PATH"] = store;
            info.Environment["MODEL_PROVIDER__MODE"] = "fake";
        }

        return info;
    }

    private async Task WaitHealthyAsync(Process process, Uri url, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = url, Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + Startup;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"The {backend} backend exited during startup (exit code {process.ExitCode}).");
            }

            try
            {
                using var response = await client.GetAsync(new Uri("health", UriKind.Relative), cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                // Not listening yet.
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"The {backend} backend did not become healthy within {Startup.TotalSeconds:0} s.");
    }

    private void StopProcesses()
    {
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true); // dotnet run and uv run start the server as a child
                    process.WaitForExit(15_000);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }

        processes.Clear();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
