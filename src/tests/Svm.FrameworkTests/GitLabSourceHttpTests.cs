using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Svm.FileStorage;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Releases;
using Xunit;
using static Svm.FrameworkTests.PackageHttpTests;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class GitLabSourceHttpTests
{
    [Fact] public async Task AuthorizedBindingThroughHttpsDrivesTwoWorkersToOneFormalReleaseAndSurvivesRestart()
    {
        await using var f = await PackageHostFixture.CreateAsync(); await using var protocol = await GitLabProtocolFixture.StartAsync();
        var cookies = new CookieContainer(); using var client = f.Client(cookies); var setup = await Setup(f, client); var software = setup.Software.Id;
        var legacy = protocol.Config(software); var connection = new GitLabConnection(Guid.NewGuid(), "来源 API 夹具", legacy.Import.Bindings[0].BaseUri, [new(17, "正式项目", ["fixture"])]);
        var options = legacy with { Import = legacy.Import with { Bindings = [], Connections = [connection] }, Credentials = [],
            ConnectionCredentials = [new(connection.Id, protocol.ApiToken, protocol.PackageToken)] };
        await f.ConfigureGitLabAsync(options);
        var path = f.ApiA.Url + $"/api/v1/manage/software/{software}/release-source";
        using (var forbidden = await client.GetAsync(path + "-options")) Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        var session = await Get<JsonElement>(client, f.ApiA.Url + "/api/v1/session"); var user = await Get<UserView>(client, f.ApiA.Url + "/api/v1/manage/users/" + session.GetProperty("subjectId").GetGuid());
        await Write<UserView>(client, f.ApiA.Url, setup.Csrf, $"subjects/{user.Id}/permissions", new { expectedRevision = user.Revision, reason = "来源管理显式授权", permissions = user.Permissions.Append(new(software, "release.source.manage")) }, HttpMethod.Put);
        var candidates = await Get<ReleaseSourceOptionsView>(client, path + "-options?pageSize=1"); Assert.Single(candidates.Items); Assert.Equal("Manual", candidates.Current.Kind);
        using (var csrf = await client.PutAsJsonAsync(path, new { connectionId = connection.Id, projectId = 17, packageName = "fixture", enabled = true, reason = "fixture", expectedRevision = 0 })) Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        using (var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(new { connectionId = connection.Id, projectId = 17, packageName = "fixture", enabled = true, reason = "fixture", expectedRevision = 0, token = "must-not-be-accepted" }) })
        {
            request.Headers.Add("X-CSRF-TOKEN", setup.Csrf); request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Contains("UNKNOWN_FIELD", await response.Content.ReadAsStringAsync());
        }
        var key = Guid.NewGuid(); var body = new { connectionId = connection.Id, projectId = 17, packageName = "fixture", enabled = true, reason = "HTTP 来源绑定", expectedRevision = 0 };
        var saved = await Write<ReleaseSourceView>(client, f.ApiA.Url, setup.Csrf, $"software/{software}/release-source", body, HttpMethod.Put, key);
        Assert.True(saved.Managed); Assert.True(saved.ConfigurationAvailable); Assert.Equal(1, saved.Revision);
        using var clientB = f.ApiB.Client(cookies);
        Assert.Equal(saved.BindingId, (await Write<ReleaseSourceView>(clientB, f.ApiB.Url, setup.Csrf, $"software/{software}/release-source", body, HttpMethod.Put, key)).BindingId);
        var a = await f.WorkerAsync(f.A); var b = await f.WorkerAsync(f.B);
        async Task<ReleaseSourceView> Source() => await Get<ReleaseSourceView>(client, path);
        await OutboxFixture.WaitAsync(async () => {
            var source = await Source(); if (source.LatestWorkId is not { } id) return false;
            return (await Get<ReleaseSyncWorkView>(client, f.ApiA.Url + "/api/v1/manage/release-sync-work/" + id)).Stage == "Completed" && await f.Personnel.CountAsync("rel.releases") == 1;
        }, 90);
        var releaseId = await PersistenceDatabase.ScalarAsync<Guid>(f.Personnel.Database.ReaderConnection, "SELECT \"Id\" FROM rel.releases");
        await OutboxFixture.WaitAsync(async () => (await Get<ReleaseView>(client, f.ApiA.Url + "/api/v1/manage/releases/" + releaseId)).State == "Formal", 60);
        var release = await Get<ReleaseView>(client, f.ApiA.Url + "/api/v1/manage/releases/" + releaseId); Assert.Equal(protocol.Hash, release.Source!.Manifest.Sha256); Assert.Null(release.TestEvidenceId);
        Assert.Equal(protocol.Bytes, await File.ReadAllBytesAsync(f.Replica(f.A, release.PackageId))); Assert.Equal(protocol.Bytes, await File.ReadAllBytesAsync(f.Replica(f.B, release.PackageId)));
        var current = await Source(); await Write<ReleaseSourceView>(client, f.ApiA.Url, setup.Csrf, $"software/{software}/release-source", new { connectionId = connection.Id, projectId = 17, packageName = "fixture", enabled = false, reason = "停用新增扫描", expectedRevision = current.Revision }, HttpMethod.Put);
        await a.DisposeAsync(); await b.DisposeAsync(); await f.WorkerAsync(f.A); await f.WorkerAsync(f.B);
        Assert.False((await Source()).Enabled); Assert.Equal(1, await f.Personnel.CountAsync("rel.releases"));
        Assert.False(protocol.CredentialLeak); foreach (var secret in new[] { protocol.ApiToken, protocol.PackageToken }) { f.ApiA.AssertRedacted(secret); f.ApiB.AssertRedacted(secret); }
    }
}
