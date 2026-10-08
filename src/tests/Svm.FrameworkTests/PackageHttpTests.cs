using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Packages;
using Xunit;
namespace Svm.FrameworkTests;
[Trait("Category", "Business")]
public sealed class PackageHttpTests
{
    [Fact] public async Task RealConsumerTwoCopiesNginxRangesAuditRepairAndDisable()
    {
        await using var f = await PackageHostFixture.CreateAsync(); var cookies = new CookieContainer(); using var client = f.Client(cookies); var setup = await Setup(f, client);
        var bytes = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 37); var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var r = await Write<ReleaseUploadResult>(client, f.ApiA.Url, setup.Csrf, $"software/{setup.Software.Id}/releases", new { changeLevel = "Patch", changeSummary = "HTTPS验证更新", changeReason = "真实双副本验证", package = new { fileName = "fixture.zip", sizeBytes = bytes.Length, sha256 = digest } }); Assert.Equal("1.0.0", r.Release.Version);
        using (var missingCsrf = await PutBytes(client, f.ApiA.Url + r.UploadPath, bytes, "invalid")) Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
        using (var bad = await PutBytes(client, f.ApiA.Url + r.UploadPath, bytes.Take(200).ToArray(), setup.Csrf)) Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        using (var uploaded = await PutBytes(client, f.ApiA.Url + r.UploadPath, bytes, setup.Csrf)) { Assert.Equal(HttpStatusCode.Accepted, uploaded.StatusCode); var view = (await uploaded.Content.ReadFromJsonAsync<PackageView>())!; Assert.Equal(bytes.Length, view.SizeBytes); }
        Assert.Equal("Staging", (await Get<ReleaseView>(client, f.ApiA.Url + "/api/v1/manage/releases/" + r.Release.Id)).State);
        var worker = await f.WorkerAsync(f.A); await OutboxFixture.WaitAsync(async () => (await Get<ReleaseView>(client, f.ApiA.Url + "/api/v1/manage/releases/" + r.Release.Id)).State == "Test", 40);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Replica(f.A, r.Release.PackageId))); Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Replica(f.B, r.Release.PackageId)));
        var before = await PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection, "SELECT count(*) FROM pkg.dispatches");
        using (var repeated = await PutBytes(client, f.ApiA.Url + r.UploadPath, bytes, setup.Csrf)) Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.Equal(before, await PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection, "SELECT count(*) FROM pkg.dispatches"));
        await f.GatewayAsync(); var path = f.GatewayUrl + "/api/v1/packages/" + r.Release.PackageId + "/content";
        using (var anonymous = f.Client(new CookieContainer())) { using var denied = await anonymous.GetAsync(path); Assert.True(denied.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden); }
        var sessions = await f.Personnel.CountAsync("pkg.download_sessions"); string etag;
        await PersistenceDatabase.ExecuteAsync(f.Personnel.Database.WriterConnection, "UPDATE pkg.replicas SET \"CheckedAt\"=now()-interval '4 minutes'");
        using (var head = await client.SendAsync(new(HttpMethod.Head, path))) { Assert.Equal(HttpStatusCode.OK, head.StatusCode); Assert.Equal(bytes.Length, head.Content.Headers.ContentLength); etag = head.Headers.ETag!.ToString(); } Assert.Equal(sessions, await f.Personnel.CountAsync("pkg.download_sessions"));
        using (var range = new HttpRequestMessage(HttpMethod.Get, path)) { range.Headers.Range = new(1, 999); range.Headers.TryAddWithoutValidation("If-Range", etag); using var response = await client.SendAsync(range); Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode); Assert.Equal(bytes.Skip(1).Take(999).ToArray(), await response.Content.ReadAsByteArrayAsync()); Assert.Equal("bytes", response.Content.Headers.ContentRange!.Unit); }
        using (var fallback = new HttpRequestMessage(HttpMethod.Get, path)) { fallback.Headers.Range = new(1, 999); fallback.Headers.TryAddWithoutValidation("If-Range", "\"obsolete\""); using var response = await client.SendAsync(fallback); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync()); }
        using (var invalid = new HttpRequestMessage(HttpMethod.Get, path)) { invalid.Headers.Range = new(bytes.Length + 1, null); using var response = await client.SendAsync(invalid); Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode); }
        using (var direct = await client.GetAsync(f.GatewayUrl + "/__svm_replicas/node-a/" + r.Release.PackageId + ".bin")) Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
        await OutboxFixture.WaitAsync(async () => await PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection, "SELECT count(*) FROM pkg.download_sessions WHERE \"EndedAt\" IS NOT NULL") == 3, 30);
        var ended = await PersistenceDatabase.ScalarAsync<long>(f.Personnel.Database.ReaderConnection, "SELECT count(*) FROM aud.events WHERE \"Operation\"='pkg.download.ended'"); Assert.Equal(3, ended);
        await worker.DisposeAsync(); File.Delete(f.Replica(f.A, r.Release.PackageId));
        using (var alternate = await client.GetAsync(path)) { Assert.Equal(HttpStatusCode.OK, alternate.StatusCode); Assert.Equal(bytes, await alternate.Content.ReadAsByteArrayAsync()); }
        await f.WorkerAsync(f.A); await OutboxFixture.WaitAsync(() => Task.FromResult(File.Exists(f.Replica(f.A, r.Release.PackageId))), 40); Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Replica(f.A, r.Release.PackageId)));
        var release = await Get<ReleaseView>(client, f.ApiA.Url + "/api/v1/manage/releases/" + r.Release.Id); await Write<ReleaseView>(client, f.ApiA.Url, setup.Csrf, $"releases/{release.Id}/disable", new { reason = "验证停止新下载", expectedRevision = release.Revision });
        using var disabled = await client.GetAsync(path); Assert.False(disabled.IsSuccessStatusCode); Assert.True(File.Exists(f.Replica(f.A, r.Release.PackageId)));
        f.ApiA.AssertRedacted(setup.Password); f.ApiB.AssertRedacted(setup.Password);
    }
    [Fact] public async Task UploadInterruptedAndHashMismatchReuseVersionAndAnotherNodeForwardsOriginalHumanProof()
    {
        await using var f = await PackageHostFixture.CreateAsync(); var cookies = new CookieContainer(); using var a = f.Client(cookies); var setup = await Setup(f, a); var bytes = RandomNumberGenerator.GetBytes(1024 * 1024 + 19); var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var r = await Write<ReleaseUploadResult>(a, f.ApiA.Url, setup.Csrf, $"software/{setup.Software.Id}/releases", new { changeLevel = "Major", changeSummary = "恢复上传", changeReason = "原版本重传", package = new { fileName = "../仅展示.zip", sizeBytes = bytes.Length, sha256 = hash } });
        using (var cancel = new CancellationTokenSource())
        using (var content = new InterruptedUpload(bytes))
        using (var request = new HttpRequestMessage(HttpMethod.Put, f.ApiA.Url + r.UploadPath) { Content = content })
        {
            request.Content.Headers.ContentType = new("application/octet-stream"); request.Headers.ExpectContinue = true; request.Headers.Add("X-CSRF-TOKEN", setup.Csrf); var pending = a.SendAsync(request, cancel.Token);
            await content.FirstChunk.Task.WaitAsync(TimeSpan.FromSeconds(15)); await OutboxFixture.WaitAsync(async () => (await Get<PackageView>(a, f.ApiA.Url + "/api/v1/manage/packages/" + r.Release.PackageId)).ProcessingStage == "Receiving", 10);
            cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        await OutboxFixture.WaitAsync(async () => (await Get<PackageView>(a, f.ApiA.Url + "/api/v1/manage/packages/" + r.Release.PackageId)).ProcessingStage == "UploadFailed", 10);
        Assert.Equal(0, await f.Personnel.CountAsync("pkg.dispatches"));
        var corrupt = bytes.ToArray(); corrupt[100] ^= 1; using (var wrong = await PutBytes(a, f.ApiA.Url + r.UploadPath, corrupt, setup.Csrf)) Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        Assert.Equal("UploadFailed", (await Get<PackageView>(a, f.ApiA.Url + "/api/v1/manage/packages/" + r.Release.PackageId)).ProcessingStage);
        using var b = f.ApiB.Client(cookies); using var repeated = await PutBytes(b, f.ApiB.Url + r.UploadPath, bytes, setup.Csrf); Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.Equal(1, await f.Personnel.CountAsync("rel.releases")); Assert.Equal(1, await f.Personnel.CountAsync("pkg.dispatches")); Assert.False(Directory.GetFiles(f.DirectoryPath, "仅展示.zip", SearchOption.AllDirectories).Any());
        await f.WorkerAsync(f.B); await OutboxFixture.WaitAsync(async () => (await Get<ReleaseView>(a, f.ApiA.Url + "/api/v1/manage/releases/" + r.Release.Id)).State == "Test", 40); Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Replica(f.A, r.Release.PackageId)));
    }
    internal static async Task<(string Csrf, string Password, SoftwareView Software)> Setup(PackageHostFixture f, HttpClient client)
    {
        var anonymous = await Get<JsonElement>(client, f.ApiA.Url + "/api/v1/session"); using var login = new HttpRequestMessage(HttpMethod.Post, f.ApiA.Url + "/api/v1/session") { Content = JsonContent.Create(new { employeeNo = PersonnelDatabase.EmployeeNo, password = f.Personnel.Password }) }; login.Headers.Add("X-CSRF-TOKEN", anonymous.GetProperty("csrfToken").GetString()); using (var response = await client.SendAsync(login)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await Get<JsonElement>(client, f.ApiA.Url + "/api/v1/session"); var password = Guid.NewGuid().ToString("N"); using var change = new HttpRequestMessage(HttpMethod.Post, f.ApiA.Url + "/api/v1/session/password") { Content = JsonContent.Create(new { currentPassword = f.Personnel.Password, newPassword = password }) }; change.Headers.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString()); using (var response = await client.SendAsync(change)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        session = await Get<JsonElement>(client, f.ApiA.Url + "/api/v1/session"); var csrf = session.GetProperty("csrfToken").GetString()!; var id = session.GetProperty("subjectId").GetGuid(); var u = await Get<UserView>(client, f.ApiA.Url + "/api/v1/manage/users/" + id);
        await Write<UserView>(client, f.ApiA.Url, csrf, $"subjects/{id}/permissions", new { permissions = u.Permissions.Append(new(null, "asset.read")).Append(new(null, "asset.manage")), expectedRevision = u.Revision, reason = "夹具台账授权" }, HttpMethod.Put);
        var software = await Write<SoftwareView>(client, f.ApiA.Url, csrf, "software", new { code = "PACKAGE-HTTP-S", name = "包接入夹具视觉", category = "Vision" }); u = await Get<UserView>(client, f.ApiA.Url + "/api/v1/manage/users/" + id);
        await Write<UserView>(client, f.ApiA.Url, csrf, $"subjects/{id}/permissions", new { permissions = u.Permissions.Concat(new[] { "release.upload", "release.disable", "audit.read", "enrollment.manage" }.Select(o => new PermissionView(software.Id, o))), expectedRevision = u.Revision, reason = "夹具版本包授权" }, HttpMethod.Put);
        return (csrf, password, software);
    }
    internal static async Task<T> Get<T>(HttpClient client, string url) { using var r = await client.GetAsync(url); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return (await r.Content.ReadFromJsonAsync<T>())!; }
    internal static async Task<T> Write<T>(HttpClient client, string url, string csrf, string path, object body, HttpMethod? method = null, Guid? key = null)
    { using var request = new HttpRequestMessage(method ?? HttpMethod.Post, url + "/api/v1/manage/" + path) { Content = JsonContent.Create(body) }; request.Headers.Add("X-CSRF-TOKEN", csrf); request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString("D")); using var r = await client.SendAsync(request); if (!r.IsSuccessStatusCode) { var error = await r.Content.ReadFromJsonAsync<JsonElement>(); throw new InvalidOperationException("Package fixture API rejected " + path.Split('/')[0] + " " + error.GetProperty("code").GetString()); } return (await r.Content.ReadFromJsonAsync<T>())!; }
    internal static Task<HttpResponseMessage> PutBytes(HttpClient client, string url, byte[] bytes, string csrf) { var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(bytes) }; request.Content.Headers.ContentType = new("application/octet-stream"); request.Headers.ExpectContinue = true; request.Headers.Add("X-CSRF-TOKEN", csrf); return client.SendAsync(request); }
    private sealed class InterruptedUpload(byte[] bytes) : HttpContent
    {
        internal TaskCompletionSource FirstChunk { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            Headers.ContentType = new("application/octet-stream"); await stream.WriteAsync(bytes.AsMemory(0, 65536), token); await stream.FlushAsync(token); FirstChunk.TrySetResult(); await Task.Delay(Timeout.Infinite, token);
        }
    }
}
