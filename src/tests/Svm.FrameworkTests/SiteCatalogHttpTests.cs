using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Identity;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class SiteCatalogHttpTests
{
    [Fact]
    public async Task RealHttpsHierarchyPreservesImmutableFieldsAndLogicalMappingHistory()
    {
        await using var db = await PersonnelDatabase.CreateAsync(); var site = new SiteCatalogOptions(Guid.NewGuid(), "HTTP 夹具厂区", "Asia/Shanghai");
        await using var api = await LocalApi.StartAsync(db.Database, site: site); using var client = api.Client(new CookieContainer());
        var session = await AuthorizeAsync(db, api, client);
        var process = await Ok(await Write(client, api.Url, "processes", new { code = "HTTP-P", name = "HTTP 工序" }, session), 201);
        var device = await Ok(await Write(client, api.Url, "devices", new { processId = Id(process), deviceNo = "HTTP-D", name = "HTTP 二期设备" }, session), 201);
        var s = await Software(client, api, session, "HTTP-S"); var sid = Id(s); var did = Id(device);
        using (var bad = await Write(client, api.Url, "software/" + sid, new { name = "更新", reason = "无资料维护权限", expectedRevision = 1 }, session, method: HttpMethod.Patch)) await Code(bad, 404, "RESOURCE_NOT_FOUND");
        var user = await Ok(await client.GetAsync(api.Url + "/api/v1/manage/users/" + Subject(session)));
        Assert.Equal(new[] { "instance.manage", "instance.read", "software.read" }, Permissions(user).Where(p => p.SoftwareId == sid).Select(p => p.Operation).Order());
        await Grant(client, api, session, user, Permissions(user).Append(new(sid, "release.upload")).ToArray());
        var changed = await Ok(await Write(client, api.Url, "software/" + sid, new { name = "更新资料", description = "安全纯文本说明", reason = "更正资料", expectedRevision = 1 }, session, method: HttpMethod.Patch));
        Assert.Equal("UpperComputer", changed.GetProperty("category").GetString()); Assert.Equal("HTTP-S", changed.GetProperty("code").GetString());
        using (var bad = await Write(client, api.Url, "software/" + sid, new { code = "MUTATED", name = "非法", reason = "不可改", expectedRevision = 2 }, session, method: HttpMethod.Patch)) await Code(bad, 400, "UNKNOWN_FIELD");
        using (var bad = await Write(client, api.Url, "devices/" + did, new { deviceNo = "MUTATED", name = "非法", reason = "不可改", expectedRevision = 1 }, session, method: HttpMethod.Patch)) await Code(bad, 400, "UNKNOWN_FIELD");
        var key = Guid.NewGuid(); var body = new { softwareId = sid, reason = "建立夹具映射" };
        var binding = await Ok(await Write(client, api.Url, $"devices/{did}/software-bindings", body, session, key), 201);
        Assert.Equal(new[] { "deviceId", "softwareId", "revision", "serverTime" }.Order(), binding.EnumerateObject().Select(p => p.Name).Order());
        var inventory = await Ok(await client.GetAsync(api.Url + $"/api/v1/manage/devices/{did}/software-inventory"));
        var item = Assert.Single(inventory.GetProperty("items").EnumerateArray()); Assert.Equal(JsonValueKind.Null, item.GetProperty("instance").ValueKind);
        Assert.DoesNotContain("installedVersion", item.GetRawText()); Assert.DoesNotContain("ipAddress", item.GetRawText());
        var deleteKey = Guid.NewGuid(); var delete = new { reason = "撤销夹具映射", expectedRevision = 1 };
        using (var removed = await Write(client, api.Url, $"devices/{did}/software-bindings/{sid}", delete, session, deleteKey, HttpMethod.Delete)) Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var active = await Ok(await Write(client, api.Url, $"devices/{did}/software-bindings", body, session), 201); Assert.Equal(3, active.GetProperty("revision").GetInt64());
        await Ok(await Write(client, api.Url, $"devices/{did}/software-bindings", body, session, key), 201);
        using (var replay = await Write(client, api.Url, $"devices/{did}/software-bindings/{sid}", delete, session, deleteKey, HttpMethod.Delete)) Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        var row = Assert.Single((await Ok(await client.GetAsync(api.Url + $"/api/v1/manage/devices/{did}/software-bindings"))).GetProperty("items").EnumerateArray()); Assert.Equal(3, row.GetProperty("revision").GetInt64());
        Assert.Equal(1, await db.CountAsync("ins.device_software_bindings"));
        using (var forbidden = await client.PostAsJsonAsync(api.Url + $"/api/v1/manage/software/{sid}/releases", new { })) Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
    }
    [Fact]
    public async Task ProtectedCursorFiltersBeforePaginationAndBindsRevisionActorSiteAndRoute()
    {
        await using var db = await PersonnelDatabase.CreateAsync(); var site = new SiteCatalogOptions(Guid.NewGuid(), "分页夹具厂区", "Asia/Shanghai");
        await using var api = await LocalApi.StartAsync(db.Database, site: site); await using var otherSite = await LocalApi.StartAsync(db.Database, site: site with { SiteId = Guid.NewGuid() });
        using var client = api.Client(new CookieContainer()); var session = await AuthorizeAsync(db, api, client);
        var a = await Software(client, api, session, "A"); await Software(client, api, session, "B");
        var page = await Ok(await client.GetAsync(api.Url + "/api/v1/manage/software?pageSize=1")); var cursor = Uri.EscapeDataString(page.GetProperty("nextCursor").GetString()!);
        var next = await Ok(await client.GetAsync(api.Url + "/api/v1/manage/software?pageSize=1&cursor=" + cursor)); Assert.Equal("B", Assert.Single(next.GetProperty("items").EnumerateArray()).GetProperty("code").GetString());
        foreach (var path in new[] { "software?pageSize=2&cursor=" + cursor, "software?name=changed&pageSize=1&cursor=" + cursor,
            "permission-options?pageSize=1&cursor=" + cursor, "software?pageSize=1&cursor=forged", "software?pageSize=1&pageSize=1" })
        { using var invalid = await client.GetAsync(api.Url + "/api/v1/manage/" + path); await Code(invalid, 400, "INVALID_REQUEST"); }
        using (var invalid = await client.GetAsync(otherSite.Url + "/api/v1/manage/software?pageSize=1&cursor=" + cursor)) await Code(invalid, 400, "INVALID_REQUEST");
        using (var invalid = await client.GetAsync(api.Url + "/api/v1/manage/software?pageSize=201")) await Code(invalid, 422, "VALIDATION_FAILED");
        var temporary = Guid.NewGuid().ToString("N");
        var person = await Ok(await Write(client, api.Url, "users", new { employeeNo = "CURSOR-READER", displayName = "分页夹具人员", temporaryPassword = temporary }, session), 201);
        await Grant(client, api, session, person, [new(null, "identity.manage")]); using var personClient = api.Client(new CookieContainer());
        await LoginAndChange(personClient, api.Url, "CURSOR-READER", temporary);
        using (var invalid = await personClient.GetAsync(api.Url + "/api/v1/manage/software?pageSize=1&cursor=" + cursor)) await Code(invalid, 400, "INVALID_REQUEST");
        var candidate = await Ok(await personClient.GetAsync(api.Url + "/api/v1/manage/permission-options?pageSize=1"));
        Assert.Equal(new[] { "id", "code", "name", "category" }.Order(), Assert.Single(candidate.GetProperty("items").EnumerateArray()).EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(13, candidate.GetProperty("softwareOperations").GetArrayLength());
        Assert.Empty((await Ok(await personClient.GetAsync(api.Url + "/api/v1/manage/software"))).GetProperty("items").EnumerateArray());
        var user = await Ok(await client.GetAsync(api.Url + "/api/v1/manage/users/" + Subject(session)));
        await Grant(client, api, session, user, Permissions(user).Where(p => p.SoftwareId != Id(a) || p.Operation != "software.read").ToArray());
        using (var invalid = await client.GetAsync(api.Url + "/api/v1/manage/software?pageSize=1&cursor=" + cursor)) await Code(invalid, 400, "INVALID_REQUEST");
        var fresh = await Ok(await client.GetAsync(api.Url + "/api/v1/manage/software?pageSize=1")); Assert.Single(fresh.GetProperty("items").EnumerateArray()); Assert.Equal(JsonValueKind.Null, fresh.GetProperty("nextCursor").ValueKind);
        using (var denied = await client.GetAsync(api.Url + "/api/v1/manage/software/" + Id(a))) await Code(denied, 404, "RESOURCE_NOT_FOUND");
        api.AssertRedacted(temporary); api.AssertRedacted(Token(session));
    }
    [Fact]
    public async Task StrictFieldsCsrfRevisionAndConfigurationRejectWithoutWriting()
    {
        await using var db = await PersonnelDatabase.CreateAsync(); await using var api = await LocalApi.StartAsync(db.Database, site: new(Guid.NewGuid(), "严格字段夹具", "Asia/Shanghai"));
        await using var disabled = await LocalApi.StartAsync(db.Database); using var client = api.Client(new CookieContainer()); var session = await AuthorizeAsync(db, api, client);
        using (var noConfig = await client.GetAsync(disabled.Url + "/api/v1/manage/site")) await Code(noConfig, 503, "CONFIGURATION_INVALID");
        var body = new { code = "STRICT", name = "夹具", category = "Vision" };
        using (var noCsrf = await client.PostAsJsonAsync(api.Url + "/api/v1/manage/software", body)) await Code(noCsrf, 403, "PERMISSION_DENIED");
        using (var unknown = await Write(client, api.Url, "software", new { body.code, body.name, body.category, subjectId = Subject(session) }, session)) await Code(unknown, 400, "UNKNOWN_FIELD");
        using (var type = await Write(client, api.Url, "software", new { body.code, body.name, category = "PLC" }, session)) await Code(type, 422, "VALIDATION_FAILED");
        using (var duplicate = await Raw(client, api.Url, "processes", """{"code":"A","code":"B","name":"fixture"}""", session)) await Code(duplicate, 400, "INVALID_REQUEST");
        using (var unknownQuery = await client.GetAsync(api.Url + "/api/v1/manage/processes?siteId=" + Guid.NewGuid())) await Code(unknownQuery, 400, "INVALID_REQUEST");
        Assert.Equal(0, await db.CountAsync("rel.software")); Assert.Equal(0, await db.CountAsync("ins.processes"));
        var p = await Ok(await Write(client, api.Url, "processes", new { code = "P", name = "工序" }, session), 201);
        using (var revision = await Write(client, api.Url, "processes/" + Id(p), new { name = "工序修改", reason = "更正" }, session, method: HttpMethod.Patch)) await Code(revision, 400, "REVISION_REQUIRED");
        using (var noKey = await Raw(client, api.Url, "processes", """{"code":"P2","name":"fixture"}""", session, idempotent: false)) await Code(noKey, 400, "INVALID_REQUEST");
    }
    internal static async Task<JsonElement> AuthorizeAsync(PersonnelDatabase db, LocalApi api, HttpClient client)
    {
        var session = await LoginAndChange(client, api.Url, PersonnelDatabase.EmployeeNo, db.Password);
        var user = await Ok(await client.GetAsync(api.Url + "/api/v1/manage/users/" + Subject(session)));
        await Grant(client, api, session, user, [new(null, "identity.manage"), new(null, "software.create"), new(null, "asset.read"), new(null, "asset.manage")]); return session;
    }
    internal static async Task<JsonElement> LoginAndChange(HttpClient client, string url, string employeeNo, string password)
    {
        var anonymous = await Ok(await client.GetAsync(url + "/api/v1/session"));
        var session = await Ok(await Raw(client, url, "/api/v1/session", JsonSerializer.Serialize(new { employeeNo, password }), anonymous, idempotent: false, absolutePath: true));
        return await Ok(await Raw(client, url, "/api/v1/session/password", JsonSerializer.Serialize(new { currentPassword = password, newPassword = Guid.NewGuid().ToString("N") }), session, idempotent: false, absolutePath: true));
    }
    internal static Task<JsonElement> Grant(HttpClient client, LocalApi api, JsonElement session, JsonElement user, PermissionView[] permissions) =>
        Ok(Write(client, api.Url, "subjects/" + Id(user) + "/permissions", new { permissions, expectedRevision = user.GetProperty("revision").GetInt64(), reason = "夹具显式授权" }, session, method: HttpMethod.Put));
    private static Task<JsonElement> Software(HttpClient c, LocalApi a, JsonElement session, string code) =>
        Ok(Write(c, a.Url, "software", new { code, name = "软件" + code, category = "UpperComputer", description = "夹具说明" }, session), 201);
    private static PermissionView[] Permissions(JsonElement user) => user.GetProperty("permissions").EnumerateArray().Select(p =>
        new PermissionView(p.GetProperty("softwareId").ValueKind == JsonValueKind.Null ? null : p.GetProperty("softwareId").GetGuid(), p.GetProperty("operation").GetString()!)).ToArray();
    private static Guid Id(JsonElement x) => x.GetProperty("id").GetGuid(); private static Guid Subject(JsonElement x) => x.GetProperty("subjectId").GetGuid();
    private static string Token(JsonElement x) => x.GetProperty("csrfToken").GetString()!;
    private static Task<HttpResponseMessage> Write(HttpClient client, string url, string path, object body, JsonElement session, Guid? key = null, HttpMethod? method = null) =>
        Raw(client, url, path, JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), session, key, method);
    private static Task<HttpResponseMessage> Raw(HttpClient client, string url, string path, string body, JsonElement session, Guid? key = null, HttpMethod? method = null, bool idempotent = true, bool absolutePath = false)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Post, url + (absolutePath ? path : "/api/v1/manage/" + path)) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-CSRF-TOKEN", Token(session)); if (idempotent) request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString()); return client.SendAsync(request);
    }
    private static async Task<JsonElement> Ok(Task<HttpResponseMessage> response, int status = 200) => await Ok(await response, status);
    private static async Task<JsonElement> Ok(HttpResponseMessage response, int status = 200)
    { using (response) { Assert.Equal(status, (int)response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore); return await response.Content.ReadFromJsonAsync<JsonElement>(); } }
    private static async Task Code(HttpResponseMessage response, int status, string code)
    { Assert.Equal(status, (int)response.StatusCode); Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()); }
}
