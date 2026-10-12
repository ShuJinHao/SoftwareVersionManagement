using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Svm.Services.Contracts.Packages;
using Xunit;
using static Svm.FrameworkTests.PackageHttpTests;
namespace Svm.FrameworkTests;
[Trait("Category", "Business")]
public sealed class PackageRecoveryTests
{
    [Fact] public async Task ExitAfterConsumerAckAndPartialCopyThenTwoWorkersResumeWithFencesAndPersistentDedup()
    {
        await using var f = await PackageHostFixture.CreateAsync(copyProxy: true); using var client = f.Client(new CookieContainer()); var setup = await Setup(f, client); var bytes = RandomNumberGenerator.GetBytes(8 * 1024 * 1024 + 23); var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var r = await Write<ReleaseUploadResult>(client, f.ApiA.Url, setup.Csrf, $"software/{setup.Software.Id}/releases", new { changeLevel = "Patch", changeSummary = "中断恢复", changeReason = "ACK后退出及复制中退出", package = new { fileName = "recovery.zip", sizeBytes = bytes.Length, sha256 = hash } });
        using (var uploaded = await PutBytes(client, f.ApiA.Url + r.UploadPath, bytes, setup.Csrf)) Assert.Equal(HttpStatusCode.Accepted, uploaded.StatusCode);
        var messageBody = await PersistenceDatabase.ScalarAsync<string>(f.Personnel.Database.ReaderConnection, "SELECT \"Body\" FROM framework.\"OutboxMessage\" LIMIT 1");
        f.CopyProxy!.HoldNextCopy(); var worker = await f.WorkerAsync(f.A); await f.CopyProxy.PartialCopy.WaitAsync(TimeSpan.FromSeconds(35));
        Assert.True(await PersistenceDatabase.ScalarAsync<bool>(f.Personnel.Database.ReaderConnection, "SELECT \"Accepted\" FROM pkg.works LIMIT 1")); Assert.True(await PersistenceDatabase.ScalarAsync<bool>(f.Personnel.Database.ReaderConnection, "SELECT EXISTS(SELECT 1 FROM framework.\"InboxState\" WHERE \"Consumed\" IS NOT NULL)"));
        await worker.DisposeAsync(); f.CopyProxy.Resume(); Assert.Equal("Staging", (await Get<ReleaseView>(client, f.ApiA.Url + "/api/v1/manage/releases/" + r.Release.Id)).State);
        Assert.True(File.Exists(f.Replica(f.A, r.Release.PackageId))); Assert.False(File.Exists(f.Replica(f.B, r.Release.PackageId)));
        await f.WorkerAsync(f.A); await f.WorkerAsync(f.B); await OutboxFixture.WaitAsync(async () => (await Get<ReleaseView>(client, f.ApiA.Url + "/api/v1/manage/releases/" + r.Release.Id)).State == "Test", 50);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Replica(f.A, r.Release.PackageId))); Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Replica(f.B, r.Release.PackageId)));
        Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection, "SELECT count(*) FROM aud.events WHERE \"Operation\"='pkg.work.completed'"));
        // The native Inbox window is gone; the immutable dispatch + accepted work fact still stops side effects.
        await PersistenceDatabase.ExecuteAsync(f.Personnel.Database.MigrationConnection, "DELETE FROM framework.\"InboxState\"");
        var envelope = JsonDocument.Parse(messageBody).RootElement; string raw;
        if (envelope.TryGetProperty("message", out _)) raw = messageBody;
        else { var id = envelope.GetProperty("eventId").GetGuid(); raw = JsonSerializer.Serialize(new { messageId = id, messageType = new[] { "urn:message:Svm.Services.Contracts.Messaging.V1:PackageWorkAvailableV1" }, message = envelope }); }
        await f.Broker.PublishRawAsync("svm.package-work.available.v1", raw); await OutboxFixture.WaitAsync(async () => await PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection, "SELECT count(*) FROM framework.\"InboxState\" WHERE \"Consumed\" IS NOT NULL") > 0, 30);
        Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection, "SELECT count(*) FROM aud.events WHERE \"Operation\"='pkg.work.completed'"));
        Assert.Equal(1, await f.Personnel.CountAsync("pkg.dispatches"));
    }
}
