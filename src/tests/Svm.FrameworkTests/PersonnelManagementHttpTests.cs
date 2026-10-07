using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class PersonnelManagementHttpTests
{
    [Fact]
    public async Task HttpsManagementSupportsSafeReplayStrictFieldsAndSharedSessionRevocation()
    {
        await using var fixture = await PersonnelDatabase.CreateAsync();
        await using var a = await LocalApi.StartAsync(fixture.Database); await using var b = await LocalApi.StartAsync(fixture.Database);
        using var admin = a.Client(new CookieContainer());
        var session = await Login(admin, a.Url, PersonnelDatabase.EmployeeNo, fixture.Password);
        using (var restricted = await admin.GetAsync(a.Url + "/api/v1/manage/users")) await Code(restricted, 403, "PERMISSION_DENIED");
        session = await Change(admin, a.Url, session, fixture.Password);
        var temporary = Guid.NewGuid().ToString("N"); var key = Guid.NewGuid();
        var input = new { employeeNo = "HTTP-USER", displayName = "HTTP 人员", temporaryPassword = temporary };
        var user = await Ok(await Write(admin, a.Url, HttpMethod.Post, "/api/v1/manage/users", input, session, key), 201);
        var id = user.GetProperty("id").GetGuid();
        var replay = await Ok(await Raw(admin, b.Url, HttpMethod.Post, "/api/v1/manage/users", JsonSerializer.Serialize(new { temporaryPassword = temporary, displayName = "HTTP 人员", employeeNo = "HTTP-USER" }), session, key), 201);
        Assert.Equal(id, replay.GetProperty("id").GetGuid()); Assert.DoesNotContain(temporary, user.GetRawText());
        using (var conflict = await Write(admin, b.Url, HttpMethod.Post, "/api/v1/manage/users", new { employeeNo = "OTHER", displayName = "HTTP 人员", temporaryPassword = temporary }, session, key)) await Code(conflict, 409, "IDEMPOTENCY_CONFLICT");
        using (var missingCsrf = await admin.PostAsJsonAsync(a.Url + "/api/v1/manage/users", input)) await Code(missingCsrf, 403, "PERMISSION_DENIED");
        using (var unknown = await Write(admin, a.Url, HttpMethod.Post, "/api/v1/manage/users", new { employeeNo = "OTHER", displayName = "人员", temporaryPassword = temporary, role = "admin" }, session)) await Code(unknown, 400, "UNKNOWN_FIELD");
        using (var duplicate = await Raw(admin, a.Url, HttpMethod.Post, "/api/v1/manage/users", """{"employeeNo":"A","employeeNo":"B","displayName":"人员","temporaryPassword":"fixture password"}""", session)) await Code(duplicate, 400, "INVALID_REQUEST");
        using (var missing = await Write(admin, a.Url, HttpMethod.Patch, "/api/v1/manage/users/" + id, new { displayName = "改名", reason = "资料更新" }, session)) await Code(missing, 400, "REVISION_REQUIRED");
        using (var bad = await Write(admin, a.Url, HttpMethod.Patch, "/api/v1/manage/users/" + id, new { expectedRevision = "1", displayName = "改名", reason = "资料更新" }, session)) await Code(bad, 400, "INVALID_REQUEST");
        using (var renamed = await Write(admin, a.Url, HttpMethod.Patch, "/api/v1/manage/users/" + id, new { expectedRevision = 1, employeeNo = "CHANGE", reason = "不允许" }, session)) await Code(renamed, 400, "UNKNOWN_FIELD");
        using var person = b.Client(new CookieContainer()); var personSession = await Login(person, b.Url, "HTTP-USER", temporary);
        personSession = await Change(person, b.Url, personSession, temporary);
        var current = await Ok(await admin.GetAsync(a.Url + "/api/v1/manage/users/" + id));
        var resetPassword = Guid.NewGuid().ToString("N");
        var reset = await Ok(await Write(admin, a.Url, HttpMethod.Post, "/api/v1/manage/users/" + id + "/reset-password",
            new { temporaryPassword = resetPassword, reason = "身份核实后重置", expectedRevision = current.GetProperty("revision").GetInt64() }, session));
        Assert.True(reset.GetProperty("mustChangePassword").GetBoolean());
        using (var revoked = await person.GetAsync(a.Url + "/api/v1/session")) await Code(revoked, 401, "AUTHENTICATION_REQUIRED");
        Assert.False((await Ok(await person.GetAsync(b.Url + "/api/v1/session"))).GetProperty("authenticated").GetBoolean());
        personSession = await Login(person, b.Url, "HTTP-USER", resetPassword);
        var disabled = await Ok(await Write(admin, b.Url, HttpMethod.Patch, "/api/v1/manage/users/" + id,
            new { isEnabled = false, reason = "暂停访问", expectedRevision = reset.GetProperty("revision").GetInt64() }, session));
        Assert.False(disabled.GetProperty("isEnabled").GetBoolean());
        using (var revoked = await person.GetAsync(a.Url + "/api/v1/session")) await Code(revoked, 401, "AUTHENTICATION_REQUIRED");
        await Ok(await Write(admin, b.Url, HttpMethod.Patch, "/api/v1/manage/users/" + id,
            new { isEnabled = true, reason = "恢复访问", expectedRevision = disabled.GetProperty("revision").GetInt64() }, session));
        Assert.False((await Ok(await person.GetAsync(a.Url + "/api/v1/session"))).GetProperty("authenticated").GetBoolean());
        foreach (var secret in new[] { fixture.Password, temporary, resetPassword, Token(session), Token(personSession) })
        { a.AssertRedacted(secret); b.AssertRedacted(secret); }
        Assert.Equal(2, await fixture.CountAsync("iam.users"));
    }
    [Fact]
    public async Task CursorBindsActorFilterAndSizeAndRevocationPrecedesReplay()
    {
        await using var fixture = await PersonnelDatabase.CreateAsync();
        await using var a = await LocalApi.StartAsync(fixture.Database); using var admin = a.Client(new CookieContainer());
        var session = await Login(admin, a.Url, PersonnelDatabase.EmployeeNo, fixture.Password); session = await Change(admin, a.Url, session, fixture.Password);
        var key = Guid.NewGuid(); var temporary = Guid.NewGuid().ToString("N");
        var input = new { employeeNo = "PAGE-ADMIN", displayName = "分页管理员", temporaryPassword = temporary };
        var user = await Ok(await Write(admin, a.Url, HttpMethod.Post, "/api/v1/manage/users", input, session, key), 201);
        var id = user.GetProperty("id").GetGuid();
        await Ok(await Write(admin, a.Url, HttpMethod.Put, "/api/v1/manage/subjects/" + id + "/permissions",
            new { permissions = new[] { new { softwareId = (Guid?)null, operation = "identity.manage" } }, reason = "管理员职责", expectedRevision = 1 }, session));
        var page = await Ok(await admin.GetAsync(a.Url + "/api/v1/manage/users?pageSize=1"));
        var cursor = page.GetProperty("nextCursor").GetString()!;
        Assert.Single((await Ok(await admin.GetAsync(a.Url + "/api/v1/manage/users?pageSize=1&cursor=" + Uri.EscapeDataString(cursor)))).GetProperty("items").EnumerateArray());
        foreach (var path in new[] { "?pageSize=1&employeeNo=PAGE&cursor=" + Uri.EscapeDataString(cursor), "?pageSize=2&cursor=" + Uri.EscapeDataString(cursor), "?pageSize=1&cursor=forged" })
        { using var bad = await admin.GetAsync(a.Url + "/api/v1/manage/users" + path); await Code(bad, 400, "INVALID_REQUEST"); }
        using (var bad = await admin.GetAsync(a.Url + "/api/v1/manage/users?pageSize=201")) await Code(bad, 422, "VALIDATION_FAILED");
        using var other = a.Client(new CookieContainer()); var otherSession = await Login(other, a.Url, "PAGE-ADMIN", temporary); await Change(other, a.Url, otherSession, temporary);
        using (var bad = await other.GetAsync(a.Url + "/api/v1/manage/users?pageSize=1&cursor=" + Uri.EscapeDataString(cursor))) await Code(bad, 400, "INVALID_REQUEST");
        var adminId = session.GetProperty("subjectId").GetGuid(); var current = await Ok(await admin.GetAsync(a.Url + "/api/v1/manage/users/" + adminId));
        await Ok(await Write(admin, a.Url, HttpMethod.Put, "/api/v1/manage/subjects/" + adminId + "/permissions",
            new { permissions = Array.Empty<object>(), reason = "移交职责", expectedRevision = current.GetProperty("revision").GetInt64() }, session));
        using (var denied = await Write(admin, a.Url, HttpMethod.Post, "/api/v1/manage/users", input, session, key)) await Code(denied, 403, "PERMISSION_DENIED");
        using (var denied = await admin.GetAsync(a.Url + "/api/v1/manage/users")) await Code(denied, 403, "PERMISSION_DENIED");
        using (var unknown = await admin.GetAsync(a.Url + "/api/v1/unknown"))
        { Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode); Assert.Equal("application/json", unknown.Content.Headers.ContentType?.MediaType); }
        using (var bearer = new HttpRequestMessage(HttpMethod.Get, a.Url + "/api/v1/manage/users"))
        { bearer.Headers.Add("Authorization", "Bearer fabricated"); using var response = await admin.SendAsync(bearer); await Code(response, 401, "CREDENTIAL_INVALID"); }
    }
    private static string Token(JsonElement session) => session.GetProperty("csrfToken").GetString()!;
    private static async Task<JsonElement> Login(HttpClient client, string url, string employeeNo, string password)
    {
        var anonymous = await Ok(await client.GetAsync(url + "/api/v1/session"));
        return await Ok(await Write(client, url, HttpMethod.Post, "/api/v1/session", new { employeeNo, password }, anonymous, idempotent: false));
    }
    private static async Task<JsonElement> Change(HttpClient client, string url, JsonElement session, string password) =>
        await Ok(await Write(client, url, HttpMethod.Post, "/api/v1/session/password", new { currentPassword = password, newPassword = Guid.NewGuid().ToString("N") }, session, idempotent: false));
    private static Task<HttpResponseMessage> Write(HttpClient client, string url, HttpMethod method, string path, object body, JsonElement session, Guid? key = null, bool idempotent = true) =>
        Raw(client, url, method, path, JsonSerializer.Serialize(body), session, key, idempotent);
    private static Task<HttpResponseMessage> Raw(HttpClient client, string url, HttpMethod method, string path, string body, JsonElement session, Guid? key = null, bool idempotent = true)
    {
        var request = new HttpRequestMessage(method, url + path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-CSRF-TOKEN", Token(session)); if (idempotent) request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return client.SendAsync(request);
    }
    private static async Task<JsonElement> Ok(HttpResponseMessage response, int status = 200)
    {
        using (response) { Assert.Equal(status, (int)response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore); return await response.Content.ReadFromJsonAsync<JsonElement>(); }
    }
    private static async Task Code(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
