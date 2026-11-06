using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Svm.Dapper;
using Svm.FileStorage;
using Svm.ServiceDefaults;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class OperationsRecoveryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ColdBackupRestoresBytesIdentityAndHistoryWithoutStartingOrReusingTheSourceProject()
    {
        using var f = new BackupFixture(); await f.Seed();
        var manifest = await DeploymentBackup.SealAsync(new(f.Source, f.Backup, "linux/arm64"), default);
        Assert.Contains(manifest.Files, e => e.Path == "packages/node-a/download-cursors/checkpoint.unknown");
        await DeploymentBackup.RestoreAsync(new(f.Backup, f.Restored, "linux/arm64"), default);
        Assert.Equal("credential-key", await File.ReadAllTextAsync(Path.Combine(f.Restored, "runtime/node-a/protection.pfx")));
        Assert.Equal("old-message", await File.ReadAllTextAsync(Path.Combine(f.Restored, "rabbitmq/data/messages")));
        var source = await File.ReadAllTextAsync(Path.Combine(f.Source, "deployment.json"));
        var restored = await File.ReadAllTextAsync(Path.Combine(f.Restored, "deployment.json"));
        Assert.Contains("svm-demo-123456789abc", source); Assert.Contains("svm-restore-", restored);
        var fence = new RecoveryFence(Path.Combine(f.Restored, "recovery")); Assert.Equal("Isolated", fence.Mode());
        Assert.False(fence.Allows("POST", "/api/v1/client/attempts/00000000-0000-0000-0000-000000000001/start"));
        Assert.Throws<RequestRejectedException>(fence.RequireWorkerOpen);
        Assert.Equal("local-state", await File.ReadAllTextAsync(Path.Combine(f.Source, "postgres/data/global/state")));
        Assert.NotEqual(await File.ReadAllTextAsync(Path.Combine(f.Source, "gateway/nginx.conf")), await File.ReadAllTextAsync(Path.Combine(f.Restored, "gateway/nginx.conf")));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("extra")]
    [InlineData("missing-key")]
    [InlineData("platform")]
    public async Task BackupRejectsCorruptionMissingKeysUnexpectedEntriesAndDifferentPlatforms(string mode)
    {
        using var f = new BackupFixture(); await f.Seed(); await DeploymentBackup.SealAsync(new(f.Source, f.Backup, "linux/arm64"), default);
        if (mode == "bytes") await File.WriteAllTextAsync(Path.Combine(f.Backup, "content/postgres/data/global/state"), "changed");
        if (mode == "extra") await File.WriteAllTextAsync(Path.Combine(f.Backup, "content/postgres/data/extra"), "unexpected");
        if (mode == "missing-key") File.Delete(Path.Combine(f.Backup, "content/runtime/node-a/protection.pfx"));
        await Assert.ThrowsAnyAsync<IOException>(() => DeploymentBackup.RestoreAsync(new(f.Backup, f.Restored, mode == "platform" ? "linux/amd64" : "linux/arm64"), default));
        Assert.False(Directory.Exists(f.Restored));
    }

    [Fact]
    public async Task BackupRejectsRunningDatabaseOverlappingOutputExistingTargetAndLinks()
    {
        using var f = new BackupFixture(); await f.Seed();
        await File.WriteAllTextAsync(Path.Combine(f.Source, "postgres/data/postmaster.pid"), "1");
        await Assert.ThrowsAsync<IOException>(() => DeploymentBackup.SealAsync(new(f.Source, f.Backup, "linux/arm64"), default));
        File.Delete(Path.Combine(f.Source, "postgres/data/postmaster.pid"));
        await Assert.ThrowsAsync<IOException>(() => DeploymentBackup.SealAsync(new(f.Source, Path.Combine(f.Source, "nested"), "linux/arm64"), default));
        Directory.CreateDirectory(f.Backup); await File.WriteAllTextAsync(Path.Combine(f.Backup, "keep"), "untouched");
        await Assert.ThrowsAsync<IOException>(() => DeploymentBackup.SealAsync(new(f.Source, f.Backup, "linux/arm64"), default));
        Assert.Equal("untouched", await File.ReadAllTextAsync(Path.Combine(f.Backup, "keep")));
        // Unix symlink behavior is exercised on Linux/macOS; the Windows gate covers compilation.
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(f.Source, "runtime/linked"), Path.Combine(f.Source, "postgres/data/PG_VERSION"));
            await Assert.ThrowsAsync<IOException>(() => DeploymentBackup.SealAsync(new(f.Source, f.Backup + "-link", "linux/arm64"), default));
        }
    }

    [Fact]
    public async Task ReopeningRequiresReconciliationPausedWorkExpiredPermitsAndVerifiedPackageBytes()
    {
        using var f = new BackupFixture(); await f.Seed();
        await DeploymentBackup.SealAsync(new(f.Source, f.Backup, "linux/arm64"), default);
        await DeploymentBackup.RestoreAsync(new(f.Backup, f.Restored, "linux/arm64"), default);
        var recovery = Path.Combine(f.Restored, "recovery"); var fence = new RecoveryFence(recovery);
        await RecoveryApproval.ExecuteAsync("recovery-review", f.Restored, default);
        Assert.Equal("Review", fence.Mode()); Assert.True(fence.Allows("POST", "/api/v1/client/status-reports"));
        Assert.True(fence.Allows("POST", "/api/v1/manage/deployments/00000000-0000-0000-0000-000000000001/pause"));
        foreach (var path in new[] { "/api/v1/enrollment/instances", "/api/v1/manage/software", "/api/v1/client/attempts/00000000-0000-0000-0000-000000000001/start", "/internal/v1/package-replicas/content" })
            Assert.False(fence.Allows("POST", path));
        Assert.False(fence.Allows("GET", "/api/v1/manage/packages/00000000-0000-0000-0000-000000000001/download"));
        Assert.Throws<RequestRejectedException>(fence.RequireWorkerOpen);
        await File.WriteAllTextAsync(Path.Combine(recovery, "review-input.json"), JsonSerializer.Serialize(new RecoveryApproval.Evidence("fixture operator", "reviewed", "fixture evidence", true, false, true, true), Json));
        await Report(0, 0);
        await Assert.ThrowsAsync<IOException>(() => RecoveryApproval.ExecuteAsync("recovery-open", f.Restored, default));
        await File.WriteAllTextAsync(Path.Combine(recovery, "review-input.json"), JsonSerializer.Serialize(new RecoveryApproval.Evidence("fixture operator", "reviewed", "fixture evidence", true, true, true, true), Json));
        await Report(1, 0); await Assert.ThrowsAsync<IOException>(() => RecoveryApproval.ExecuteAsync("recovery-open", f.Restored, default));
        await Report(0, 1); await Assert.ThrowsAsync<IOException>(() => RecoveryApproval.ExecuteAsync("recovery-open", f.Restored, default));
        await Report(0, 0);
        var original = Path.Combine(f.Restored, "packages/node-a/replicas/" + BackupFixture.PackageId + ".bin");
        await File.WriteAllTextAsync(original, "broken");
        await Assert.ThrowsAsync<IOException>(() => RecoveryApproval.ExecuteAsync("recovery-open", f.Restored, default));
        Assert.Equal("Review", fence.Mode()); await File.WriteAllTextAsync(original, "package-bytes");
        await RecoveryApproval.ExecuteAsync("recovery-open", f.Restored, default);
        Assert.Equal("Normal", fence.Mode()); Assert.Single(Directory.GetFiles(recovery, "approval-*.json"));
        async Task Report(long deployments, long permits)
        {
            await File.WriteAllTextAsync(Path.Combine(recovery, "pre-open-report.json"), JsonSerializer.Serialize(new OperationsSnapshot(DateTimeOffset.UtcNow, "Available",
                [new("recovery.unpausedDeployments", "Available", deployments, false, null, "", []), new("recovery.validStartPermits", "Available", permits, false, null, "", [])]), Json));
            await File.WriteAllTextAsync(Path.Combine(recovery, "packages-report.json"), JsonSerializer.Serialize(new RecoveryPackages(DateTimeOffset.UtcNow,
                [new(Guid.Parse(BackupFixture.PackageId), 13, Convert.ToHexString(SHA256.HashData("package-bytes"u8)).ToLowerInvariant())]), Json));
        }
    }

    [Fact]
    public async Task InvalidRecoveryStateFailsClosedAndOrdinaryUnconfiguredHostsKeepTheirBehavior()
    {
        using var f = new BackupFixture(); Directory.CreateDirectory(f.Source);
        var fence = new RecoveryFence(f.Source); Assert.Equal("Normal", fence.Mode());
        await File.WriteAllTextAsync(Path.Combine(f.Source, "locked.json"), "invalid"); Assert.Equal("Isolated", fence.Mode());
        Assert.False(fence.Allows("GET", "/api/v1/manage/users"));
        Assert.True(new RecoveryFence(null).Allows("POST", "/api/v1/manage/users"));
    }

    [Fact]
    public async Task ReviewCanReapplyRevocationsButCannotEnablePeopleOrMintCredentials()
    {
        using var f = new BackupFixture(); Directory.CreateDirectory(f.Source);
        await File.WriteAllTextAsync(Path.Combine(f.Source, "locked.json"), "{\"state\":\"Review\"}");
        var fence = new RecoveryFence(f.Source); var id = Guid.NewGuid();
        Assert.True(fence.Allows("POST", $"/api/v1/manage/credentials/{id}/revoke"));
        Assert.False(fence.Allows("POST", "/api/v1/manage/enrollment-grants"));
        foreach (var enabled in new[] { false, true })
        {
            var context = new DefaultHttpContext(); context.Request.Method = "PATCH"; context.Request.Path = $"/api/v1/manage/users/{id}";
            context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { isEnabled = enabled, expectedRevision = 1, reason = "reconcile" }));
            context.Response.Body = new MemoryStream(); var reached = false;
            await fence.InvokeAsync(context, () => { reached = true; return Task.CompletedTask; });
            Assert.Equal(!enabled, reached); Assert.Equal(0, context.Request.Body.Position);
        }
    }

    [Fact]
    public void StructuredLogsKeepCorrelationButNeverRenderSecretsMessagesOrExceptions()
    {
        var id = Guid.NewGuid(); var state = new Dictionary<string, object?> { ["WorkId"] = id, ["Code"] = "DEPENDENCY_UNAVAILABLE",
            ["Password"] = "fixture-secret", ["{OriginalFormat}"] = "Bearer fixture-secret", ["CodeIgnored"] = "secret", ["StatusCode"] = 503 };
        var entry = new LogEntry<Dictionary<string, object?>>(LogLevel.Error, "Svm.Worker", new(17), state, new IOException("fixture-secret"), (_, _) => "fixture-secret");
        using var writer = new StringWriter(); new SafeConsoleFormatter().Write(entry, null, writer);
        Assert.DoesNotContain("fixture-secret", writer.ToString()); Assert.DoesNotContain("Password", writer.ToString());
        using var json = JsonDocument.Parse(writer.ToString()); Assert.Equal(id, json.RootElement.GetProperty("WorkId").GetGuid());
        Assert.Equal("IOException", json.RootElement.GetProperty("exceptionType").GetString());
    }

    [Fact]
    public async Task PrivateOperationsCacheUsesOneReadAndDoesNotConvertMissingEvidenceToZero()
    {
        var calls = 0; var services = new ServiceCollection(); services.AddLogging(); services.AddHealthChecks(); using var provider = services.BuildServiceProvider();
        var options = new HostRuntimeConfiguration(HealthPort: PackageHostFixture.Port());
        await using var host = new HostDiagnostics(options, "HttpApi", _ => Task.FromResult(true), new Lifetime(), provider.GetRequiredService<HealthCheckService>(), _ =>
        { calls++; return Task.FromResult(new OperationsSnapshot(DateTimeOffset.UtcNow, "Incomplete", [new("package.pending", "Unavailable", null, false, null, "CreatedAt", [])])); });
        await host.StartAsync(default);
        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            for (var i = 0; i < 2; i++)
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{options.HealthPort}/health/operations");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("metrics")[0].GetProperty("count").ValueKind);
            }
            Assert.Equal(1, calls);
        }
        finally { await host.StopAsync(default); }
    }

    [Fact]
    public async Task FileDiagnosticsReportUncollectedBytesAndMissingEvidenceWithoutReadingLogBodies()
    {
        using var f = new BackupFixture(); Directory.CreateDirectory(f.Source); var logs = Path.Combine(f.Source, "logs"); Directory.CreateDirectory(logs);
        var generation = Guid.NewGuid(); Directory.CreateDirectory(Path.Combine(f.Source, "download-cursors"));
        await File.WriteAllTextAsync(Path.Combine(logs, generation + ".jsonl"), "secret-private-log");
        await File.WriteAllTextAsync(Path.Combine(f.Source, "download-cursors", generation + ".offset"), "6");
        var options = new PackageFileOptions("node-a", f.Source, Guid.NewGuid(), "127.0.0.1", 7444, "", null, "", null, "", "", null, logs, new(123, 5), new(), [], []);
        var result = await PackageOperationsProbe.ReadAsync(options, default); Assert.Equal(12, result.Single(x => x.Name == "download.uncollectedBytes").Count);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result));
        Directory.Delete(logs, true); result = await PackageOperationsProbe.ReadAsync(options, default);
        var unavailable = result.Single(x => x.Name == "download.uncollectedBytes"); Assert.Equal("Unavailable", unavailable.Status); Assert.Null(unavailable.Count);
    }

    [Fact]
    public async Task ReadOnlyPostgresDiagnosticsCapWorkAndUseRealSchemaWithoutChangingBusinessFacts()
    {
        await using var f = await PackageFixture.CreateAsync(); var release = (await f.SendAsync(f.Create())).Value;
        await PersistenceDatabase.ExecuteAsync(f.Database.MigrationConnection, """
            INSERT INTO pkg.works SELECT (jsonb_populate_record(NULL::pkg.works,to_jsonb(w)||jsonb_build_object('Id',gen_random_uuid()))).* FROM pkg.works w CROSS JOIN generate_series(1,1005) LIMIT 1005;
            UPDATE pkg.packages SET "SizeBytes"="ExpectedSize","Sha256"="ExpectedSha256","State"='Ready';
            """);
        var before = await f.Count("pkg.works");
        await using var probe = new ReadPersistenceProbe(f.Database.ReaderConnection);
        var result = await probe.OperationsAsync(default, 120);
        Assert.True(result.Status == "Available", string.Join(',', result.Metrics.Where(m => m.Status != "Available").Select(m => m.Name)));
        var work = result.Metrics.Single(x => x.Name == "package.pending"); Assert.Equal(1000, work.Count); Assert.True(work.Capped);
        Assert.Equal(20, result.Metrics.Single(x => x.Name == "pkg.works").Examples.Count);
        Assert.Null(result.Metrics.Single(x => x.Name == "task.pending").OldestAt);
        Assert.Equal(before, await f.Count("pkg.works"));
        var inventory = await probe.RecoveryPackagesAsync(default); Assert.Equal(release.Release.PackageId, Assert.Single(inventory.Packages).Id);
        var canceled = await probe.OperationsAsync(new CancellationToken(true)); Assert.Equal("Incomplete", canceled.Status); Assert.All(canceled.Metrics, x => Assert.Null(x.Count));
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => new(true); public CancellationToken ApplicationStopping => default; public CancellationToken ApplicationStopped => default; public void StopApplication() { }
    }
    private sealed class BackupFixture : IDisposable
    {
        private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "svm-operations-" + Guid.NewGuid().ToString("N"));
        internal const string PackageId = "11111111-1111-4111-8111-111111111111";
        internal string Source => Path.Combine(_root, "source"); internal string Backup => Path.Combine(_root, "backup"); internal string Restored => Path.Combine(_root, "restored");
        internal async Task Seed()
        {
            await Write("deployment.json", JsonSerializer.Serialize(new { project = "svm-demo-123456789abc", releaseCommit = new string('a', 40), platform = "linux/arm64" }, Json));
            await Write("deployment.env", "SVM_PROJECT=svm-demo-123456789abc\nSVM_RUNTIME_IMAGE=svm/runtime:fixture\n");
            await Write(".svm-demo", "svm-demo-123456789abc\n"); await Write("svm.sh", "#!/bin/sh\n"); await Write("compose.yaml", "services: {}\n");
            await Write("recovery/quiesced.json", "{}"); await Write("postgres/data/PG_VERSION", "17\n"); await Write("postgres/data/global/state", "local-state");
            await Write("gateway/nginx.conf", "set $svm_generation 22222222-2222-4222-8222-222222222222;\nset $svm_generation 33333333-3333-4333-8333-333333333333;\n");
            await Write("runtime/node-a/protection.pfx", "credential-key"); await Write("rabbitmq/data/messages", "old-message"); await Write("image/release/release.json", "{}");
            await Write("packages/node-a/download-cursors/checkpoint.unknown", "unconfirmed");
            foreach (var node in new[] { "node-a", "node-b" }) await Write($"packages/{node}/replicas/{PackageId}.bin", "package-bytes");
        }
        private async Task Write(string relative, string value) { var path = Path.Combine(Source, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, value); }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
