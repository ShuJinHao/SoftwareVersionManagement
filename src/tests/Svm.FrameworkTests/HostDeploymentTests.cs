using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Svm.ServiceDefaults;
using Svm.Services.Contracts.Framework;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class HostDeploymentTests
{
    [Theory]
    [InlineData("192.0.2.10", "192.0.2.10", true)]
    [InlineData("192.0.2.11", "192.0.2.10", false)]
    [InlineData("127.0.0.1", null, false)]
    public async Task OnlyAnExplicitTrustedProxyChangesTheSource(string peer, string? trusted, bool applies)
    {
        var options = new HostRuntimeConfiguration(KnownProxies: trusted is null ? [] : [trusted]);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Scheme = "https";
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.17";
        context.Request.Headers["X-Forwarded-Proto"] = "http";
        using var logs = LoggerFactory.Create(_ => { });
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, logs, Options.Create(options.Forwarding()));
        await middleware.Invoke(context);
        Assert.Equal(applies ? "203.0.113.17" : peer, context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("https", context.Request.Scheme);
    }
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("untrusted-domain.invalid")]
    public void UnboundedOrInvalidProxyConfigurationIsRejected(string address)
    {
        var error = Assert.Throws<RequestRejectedException>(() => new HostRuntimeConfiguration(KnownProxies: [address]).Validate());
        Assert.Equal(RequestFailure.ConfigurationInvalid, error.Failure);
    }
    [Fact]
    public void MissingExplicitPublicCertificateIsRejectedWithoutEchoingTheValue()
    {
        var secret = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "fixture-private-secret.pfx");
        var error = Assert.Throws<RequestRejectedException>(() => new HostRuntimeConfiguration(secret, "fixture-password-secret").Validate());
        Assert.DoesNotContain("fixture-private-secret", error.ToString());
        Assert.DoesNotContain("fixture-password-secret", error.ToString());
    }
    [Theory]
    [InlineData("HttpApi", true, false, 200)]
    [InlineData("HttpApi", false, true, 503)]
    [InlineData("Worker", true, false, 503)]
    [InlineData("Worker", true, true, 200)]
    public async Task PrivateReadinessChecksOnlyDependenciesHostedByThisRole(string role, bool database, bool bus, int expected)
    {
        var calls = 0;
        var services = new ServiceCollection(); services.AddLogging();
        services.AddHealthChecks().AddCheck("fixture-bus", () => { calls++; return bus ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy(); });
        using var provider = services.BuildServiceProvider();
        var options = new HostRuntimeConfiguration(HealthPort: PackageHostFixture.Port());
        await using var diagnostics = new HostDiagnostics(options, role, _ => Task.FromResult(database), new StartedLifetime(), provider.GetRequiredService<HealthCheckService>());
        await diagnostics.StartAsync(default);
        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync("http://127.0.0.1:" + options.HealthPort + "/health/ready");
            Assert.Equal(expected, (int)response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(role == "Worker" ? 1 : 0, calls);
            if (role == "HttpApi") Assert.Equal("OutboxOnly", json.RootElement.GetProperty("messaging").GetString());
            using var live = await client.GetAsync("http://127.0.0.1:" + options.HealthPort + "/health/live");
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }
        finally { await diagnostics.StopAsync(default); }
    }
    private sealed class StartedLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => new(true);
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

}
