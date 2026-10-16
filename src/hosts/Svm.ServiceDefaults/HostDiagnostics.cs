using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Svm.ServiceDefaults;

/// <summary>Private technical listener; no business services, secrets, or public management endpoints.</summary>
public sealed class HostDiagnostics(HostRuntimeConfiguration options, string role,
    Func<CancellationToken, Task<bool>> database, IHostApplicationLifetime lifetime, HealthCheckService checks) : IHostedService, IAsyncDisposable
{
    public static async Task<int> ProbeAsync(HostRuntimeConfiguration options)
    {
        if (options.HealthPort is not { } port) return 2;
        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(6) };
            using var response = await client.GetAsync("http://127.0.0.1:" + port + "/health/ready");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException) { return 1; }
        catch (OperationCanceledException) { return 1; }
    }
    private WebApplication? _listener;
    public async Task StartAsync(CancellationToken token)
    {
        if (options.HealthPort is not { } port) return;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://0.0.0.0:" + port);
        _listener = builder.Build();
        _listener.MapGet("/health/live", () => Results.Json(new { status = "Live", role }));
        _listener.MapGet("/health/ready", async (HttpContext http) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var db = await database(timeout.Token);
            var report = role == "Worker" ? await checks.CheckHealthAsync(timeout.Token) : null;
            var messaging = report?.Status == HealthStatus.Healthy;
            var ready = lifetime.ApplicationStarted.IsCancellationRequested && db && (role != "Worker" || messaging);
            return Results.Json(new { status = ready ? "Ready" : "NotReady", role,
                database = db, messaging = role == "Worker" ? messaging ? "Ready" : "NotReady" : "OutboxOnly" }, statusCode: ready ? 200 : 503);
        });
        await _listener.StartAsync(token);
    }
    public Task StopAsync(CancellationToken token) => _listener?.StopAsync(token) ?? Task.CompletedTask;
    public async ValueTask DisposeAsync() { if (_listener is not null) await _listener.DisposeAsync(); }
}
