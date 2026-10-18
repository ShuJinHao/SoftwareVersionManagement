using System.Net;
using System.Security.Cryptography;
using Svm.Services.Contracts.Packages;
using Xunit;
using static Svm.FrameworkTests.PackageHttpTests;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class PackageWorkerIsolationTests
{
    [Fact]
    public async Task TwoCopyExecutorsDoNotStarveHealthOrDownloadLogs()
    {
        await using var f = await PackageHostFixture.CreateAsync(copyProxy: true, uploadIdleSeconds: 15);
        using var client = f.Client(new CookieContainer());
        var setup = await Setup(f, client);
        var bytes = RandomNumberGenerator.GetBytes(1024 * 1024 + 1);
        var ready = await Upload(f, client, setup.Csrf, setup.Software.Id, bytes);
        var initialWorker = await f.WorkerAsync(f.A);
        await WaitReady(f, client, ready);
        await initialWorker.DisposeAsync();
        await f.GatewayAsync();
        var pending = new[]
        {
            await Upload(f, client, setup.Csrf, setup.Software.Id, bytes),
            await Upload(f, client, setup.Csrf, setup.Software.Id, bytes)
        };
        f.CopyProxy!.HoldAllCopies();
        await f.WorkerAsync(f.A);
        await f.WorkerAsync(f.A); // Two processes on one logical node; leases arbitrate distinct work.
        await OutboxFixture.WaitAsync(() => Task.FromResult(f.CopyProxy.CopyCount == 2), 20);
        Assert.Equal(2, await Scalar<long>(f, "SELECT count(*) FROM pkg.works WHERE \"State\"='Running'"));

        using (var response = await client.GetAsync(f.GatewayUrl + "/api/v1/packages/" + ready.Release.PackageId + "/content"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        }
        // Observe a new health pass after the download's own probe has finished.
        var checkpoint = await Scalar<DateTime>(f, "SELECT clock_timestamp()");
        await OutboxFixture.WaitAsync(async () =>
            await PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection,
                "SELECT count(*) FROM pkg.replicas WHERE \"PackageId\"=@id AND \"State\"='Healthy' AND \"CheckedAt\">@checkpoint",
                new Npgsql.NpgsqlParameter("id", ready.Release.PackageId), new Npgsql.NpgsqlParameter("checkpoint", checkpoint)) == 2 &&
            await Scalar<long>(f, "SELECT count(*) FROM pkg.download_sessions WHERE \"EndedAt\" IS NOT NULL") == 1, 9);
        Assert.Equal(2, await Scalar<long>(f, "SELECT count(*) FROM pkg.works WHERE \"State\"='Running'"));
        Assert.Equal(1, await Scalar<long>(f, "SELECT count(*) FROM aud.events WHERE \"Operation\"='pkg.download.ended'"));
        Assert.True((await Get<ReleaseView>(client, f.ApiA.Url + "/api/v1/manage/releases/" + ready.Release.Id)).DownloadAvailable);
        f.CopyProxy.Resume();
        foreach (var upload in pending) await WaitReady(f, client, upload);
        Assert.Equal(3, await Scalar<long>(f, "SELECT count(*) FROM aud.events WHERE \"Operation\"='pkg.work.completed'"));
    }

    [Fact]
    public async Task ResponseTimeoutReleasesLeaseAndRestartWithExplicitRetryCompletesOriginalPackage()
    {
        await using var f = await PackageHostFixture.CreateAsync(copyProxy: true);
        using var client = f.Client(new CookieContainer());
        var setup = await Setup(f, client);
        var bytes = RandomNumberGenerator.GetBytes(1024 * 1024 + 7);
        var upload = await Upload(f, client, setup.Csrf, setup.Software.Id, bytes);
        f.CopyProxy!.HoldNextCopy();
        var worker = await f.WorkerAsync(f.A with { Execution = f.A.Execution with { PeerResponseSeconds = 2, DownloadAuthorizationSeconds = 3 } });
        await f.CopyProxy.PartialCopy.WaitAsync(TimeSpan.FromSeconds(20));
        await OutboxFixture.WaitAsync(async () => await Scalar<string>(f, "SELECT \"State\" FROM pkg.works LIMIT 1") == "Failed", 8);
        Assert.Equal("REPLICA_COPY_TIMEOUT", await Scalar<string>(f, "SELECT \"LastErrorCode\" FROM pkg.works LIMIT 1"));
        Assert.True(await Scalar<bool>(f, "SELECT \"LeaseUntil\" IS NULL FROM pkg.works LIMIT 1"));
        await worker.DisposeAsync();
        f.CopyProxy.Resume();
        var package = await Get<PackageView>(client, f.ApiA.Url + "/api/v1/manage/packages/" + upload.Release.PackageId);
        await Write<PackageView>(client, f.ApiA.Url, setup.Csrf, $"packages/{package.Id}/retry",
            new { expectedRevision = package.Revision, reason = "测试专用超时后的显式重试" });
        await f.WorkerAsync(f.A);
        await WaitReady(f, client, upload);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Replica(f.A, package.Id)));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Replica(f.B, package.Id)));
        Assert.Equal(1, await f.Personnel.CountAsync("rel.releases"));
        Assert.Equal(1, await Scalar<long>(f, "SELECT count(*) FROM aud.events WHERE \"Operation\"='pkg.work.completed'"));
    }

    private static Task<T> Scalar<T>(PackageHostFixture f, string sql) => PersistenceDatabase.ScalarAsync<T>(f.Personnel.Database.ReaderConnection, sql);
    private static Task WaitReady(PackageHostFixture f, HttpClient client, ReleaseUploadResult upload) => OutboxFixture.WaitAsync(async () =>
        (await Get<ReleaseView>(client, f.ApiA.Url + "/api/v1/manage/releases/" + upload.Release.Id)).State == "Test", 35);
    private static async Task<ReleaseUploadResult> Upload(PackageHostFixture f, HttpClient client, string csrf, Guid software, byte[] bytes)
    {
        var result = await Write<ReleaseUploadResult>(client, f.ApiA.Url, csrf, $"software/{software}/releases", new
        {
            changeLevel = "Patch", changeSummary = "虚构后台隔离验证", changeReason = "故障代理验证",
            package = new { fileName = "worker-isolation.zip", sizeBytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() }
        });
        using var uploaded = await PutBytes(client, f.ApiA.Url + result.UploadPath, bytes, csrf);
        Assert.Equal(HttpStatusCode.Accepted, uploaded.StatusCode);
        return result;
    }
}
