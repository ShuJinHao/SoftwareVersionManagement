using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Npgsql;
using Svm.ServiceDefaults;
using Svm.Services.Contracts.Catalog;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class PersonnelHttpTests
{
    [Fact]
    public async Task TwoHttpsNodesShareKeysAndRejectRevokedSessionsAndInvalidCsrf()
    {
        await using var fixture = await PersonnelDatabase.CreateAsync();
        await using var nodeA = await LocalApi.StartAsync(fixture.Database);
        await using var nodeB = await LocalApi.StartAsync(fixture.Database);
        var cookies = new CookieContainer();
        using var client = nodeA.Client(cookies);
        var anonymous = await Session(client, nodeA.Url);
        Assert.False(anonymous.GetProperty("authenticated").GetBoolean());
        var loginInput = new { employeeNo = PersonnelDatabase.EmployeeNo, password = fixture.Password };
        using (var missing = await client.PostAsJsonAsync(nodeA.Url + "/api/v1/session", loginInput))
            await Code(missing, 403, "PERMISSION_DENIED");
        using (var bad = await Write(client, HttpMethod.Post, nodeA.Url, "/api/v1/session", loginInput, "invalid"))
            await Code(bad, 403, "PERMISSION_DENIED");
        using (var extra = await Write(client, HttpMethod.Post, nodeA.Url, "/api/v1/session", new { employeeNo = "TEST", password = "anything", role = "admin" }, Token(anonymous)))
            await Code(extra, 400, "UNKNOWN_FIELD");
        using (var fake = new HttpRequestMessage(HttpMethod.Get, nodeA.Url + "/api/v1/session"))
        {
            fake.Headers.Add("Authorization", "Bearer forged");
            using var response = await client.SendAsync(fake); await Code(response, 401, "CREDENTIAL_INVALID");
        }
        var loggedIn = await Success(await Write(client, HttpMethod.Post, nodeA.Url, "/api/v1/session", loginInput, Token(anonymous)));
        Assert.True(loggedIn.GetProperty("mustChangePassword").GetBoolean());
        Assert.True(loggedIn.GetProperty("authenticated").GetBoolean());
        Assert.All(cookies.GetCookies(new Uri(nodeA.Url)).Cast<Cookie>(), cookie => { Assert.True(cookie.Secure); Assert.True(cookie.HttpOnly); });
        var oldCookies = Clone(cookies, nodeA.Url);
        using var oldClient = nodeB.Client(oldCookies);
        Assert.True((await Session(client, nodeB.Url)).GetProperty("authenticated").GetBoolean());
        var password = Guid.NewGuid().ToString("N");
        using (var stale = await Write(client, HttpMethod.Post, nodeB.Url, "/api/v1/session/password", new { currentPassword = fixture.Password, newPassword = password }, Token(anonymous)))
            await Code(stale, 403, "PERMISSION_DENIED");
        var changed = await Success(await Write(client, HttpMethod.Post, nodeB.Url, "/api/v1/session/password",
            new { currentPassword = fixture.Password, newPassword = password }, Token(loggedIn)));
        Assert.False(changed.GetProperty("mustChangePassword").GetBoolean());
        using (var revoked = await oldClient.GetAsync(nodeA.Url + "/api/v1/session")) await Code(revoked, 401, "AUTHENTICATION_REQUIRED");
        var logoutCookies = Clone(cookies, nodeA.Url);
        using var logoutOldClient = nodeA.Client(logoutCookies);
        using (var logout = await Write(client, HttpMethod.Delete, nodeA.Url, "/api/v1/session", null, Token(changed)))
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using (var revoked = await logoutOldClient.GetAsync(nodeB.Url + "/api/v1/session")) await Code(revoked, 401, "AUTHENTICATION_REQUIRED");
        var fresh = await Session(client, nodeA.Url);
        var current = await Success(await Write(client, HttpMethod.Post, nodeA.Url, "/api/v1/session", new { employeeNo = PersonnelDatabase.EmployeeNo, password }, Token(fresh)));
        await nodeB.DisposeAsync();
        await using var restarted = await LocalApi.StartAsync(fixture.Database);
        Assert.Equal(current.GetProperty("subjectId").GetGuid(), (await Session(client, restarted.Url)).GetProperty("subjectId").GetGuid());
        using var tamperedClient = restarted.Client(new CookieContainer());
        using var tampered = new HttpRequestMessage(HttpMethod.Get, restarted.Url + "/api/v1/session");
        tampered.Headers.Add("Cookie", "__Host-Svm.Session=forged-cookie");
        using (var response = await tamperedClient.SendAsync(tampered)) await Code(response, 401, "AUTHENTICATION_REQUIRED");
        await fixture.ExecuteAsync("UPDATE iam.sessions SET \"ExpiresAt\"=clock_timestamp()-interval '1 second'");
        using (var expired = await client.GetAsync(restarted.Url + "/api/v1/session")) await Code(expired, 401, "AUTHENTICATION_REQUIRED");
        var xml = await PersistenceDatabase.ScalarAsync<string>(fixture.Database.ReaderConnection, "SELECT \"Xml\" FROM framework.data_protection_keys LIMIT 1");
        Assert.Contains("encryptedSecret", xml);
        Assert.DoesNotContain("<masterKey", xml);
        foreach (var node in new[] { nodeA, nodeB, restarted })
        { node.AssertRedacted(fixture.Password); node.AssertRedacted(password); }
    }

    [Fact]
    public async Task HttpRateLimitUsesSharedAccountCounterAndSafeProblems()
    {
        await using var fixture = await PersonnelDatabase.CreateAsync();
        await using var a = await LocalApi.StartAsync(fixture.Database);
        await using var b = await LocalApi.StartAsync(fixture.Database);
        using var client = a.Client(new CookieContainer());
        var token = Token(await Session(client, a.Url));
        for (var i = 0; i < 5; i++)
        {
            using var response = await Write(client, HttpMethod.Post, i % 2 == 0 ? a.Url : b.Url, "/api/v1/session",
                new { employeeNo = PersonnelDatabase.EmployeeNo, password = "incorrect password" }, token);
            await Code(response, 401, "CREDENTIAL_INVALID");
        }
        using var limited = await Write(client, HttpMethod.Post, b.Url, "/api/v1/session", new { employeeNo = PersonnelDatabase.EmployeeNo, password = fixture.Password }, token);
        await Code(limited, 429, "RATE_LIMITED");
        Assert.True(limited.Headers.Contains("Retry-After"));
        a.AssertRedacted(fixture.Password); b.AssertRedacted(fixture.Password);
    }

    [Fact]
    public async Task DatabaseFailureReturns503WithoutSecretDiagnostics()
    {
        await using var fixture = await PersonnelDatabase.CreateAsync();
        var secret = Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(fixture.Database.WriterConnection) { Password = secret };
        await using var node = await LocalApi.StartAsync(fixture.Database, connection.ConnectionString);
        using var client = node.Client(new CookieContainer());
        using var response = await client.GetAsync(node.Url + "/api/v1/session");
        await Code(response, 503, "DEPENDENCY_UNAVAILABLE");
        Assert.True(response.Headers.Contains("Retry-After"));
        node.AssertRedacted(secret);
    }

    private static CookieContainer Clone(CookieContainer source, string url)
    {
        var target = new CookieContainer();
        foreach (Cookie cookie in source.GetCookies(new Uri(url)))
            target.Add(new Uri(url), new Cookie(cookie.Name, cookie.Value, cookie.Path) { Secure = cookie.Secure, HttpOnly = cookie.HttpOnly });
        return target;
    }
    private static string Token(JsonElement session) => session.GetProperty("csrfToken").GetString()!;
    private static Task<HttpResponseMessage> Write(HttpClient client, HttpMethod method, string url, string path, object? input, string token)
    {
        var request = new HttpRequestMessage(method, url + path);
        request.Headers.Add("X-CSRF-TOKEN", token);
        if (input is not null) request.Content = JsonContent.Create(input);
        return client.SendAsync(request);
    }
    private static async Task<JsonElement> Session(HttpClient client, string url) => await Success(await client.GetAsync(url + "/api/v1/session"));
    private static async Task<JsonElement> Success(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
    }
    private static async Task Code(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.True(body.TryGetProperty("traceId", out _));
    }
}

internal sealed class LocalApi : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _configuration;
    private readonly string _thumbprint;
    private readonly string? _siteConfiguration;
    private readonly string? _instanceConfiguration;
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly TaskCompletionSource<string> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    private readonly List<string> _packageConfigurations = [];
    private int _publicPort;
    internal string Url { get; private set; } = "";
    private LocalApi(Process process, string configuration, string thumbprint, string? siteConfiguration, string? instanceConfiguration)
    { _process = process; _configuration = configuration; _thumbprint = thumbprint; _siteConfiguration = siteConfiguration; _instanceConfiguration = instanceConfiguration; }
    internal static async Task<LocalApi> StartAsync(PersistenceDatabase database, string? writer = null, SiteCatalogOptions? site = null,
        Svm.Services.Contracts.Instances.InstanceAccessOptions? instanceAccess = null,
        Svm.FileStorage.PackageFileOptions? packages = null, Svm.EventBus.MessagingOptions? messaging = null, int publicPort = 0)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "build/postgres.local.json"))) root = root.Parent;
        if (root is null || OperatingSystem.IsWindows()) throw new InvalidOperationException("Local SVM verification requires its macOS/Linux workspace.");
        var config = Path.Combine(root.FullName, ".cache", $"personnel-api-{Guid.NewGuid():N}.json");
        await using (var file = new FileStream(config, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            await JsonSerializer.SerializeAsync(file, new { writerConnectionString = writer ?? database.WriterConnection, readerConnectionString = database.ReaderConnection });
        var personnel = PersonnelConfiguration.LoadFromEnvironment();
        var certPath = packages?.ServerCertificatePath ?? Path.Combine(Path.GetDirectoryName(personnel.CertificatePath)!, "https.pfx");
        var certPassword = packages?.ServerCertificatePassword ?? personnel.CertificatePassword;
        using var cert = new X509Certificate2(certPath, certPassword);
        var info = new ProcessStartInfo(Path.Combine(root.FullName, "eng/dotnet"))
        { WorkingDirectory = root.FullName, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(root.FullName, "src/hosts/Svm.HttpApi/bin/Debug/net8.0/Svm.HttpApi.dll"));
        info.Environment["SVM_PERSISTENCE_CONFIG_FILE"] = config;
        info.Environment["ASPNETCORE_URLS"] = packages is null ? "https://127.0.0.1:0" : $"https://0.0.0.0:{publicPort}";
        info.Environment["Kestrel__Certificates__Default__Path"] = certPath;
        info.Environment["Kestrel__Certificates__Default__Password"] = certPassword;
        info.Environment.Remove("SVM_PACKAGE_CONFIG_FILE"); info.Environment.Remove("SVM_MESSAGING_CONFIG_FILE");
        info.Environment["Logging__LogLevel__Default"] = "Information";
        info.Environment.Remove("SVM_SITE_CONFIG_FILE");
        info.Environment.Remove("SVM_INSTANCE_ACCESS_CONFIG_FILE");
        string? siteConfig = null;
        if (site is not null)
        {
            siteConfig = Path.Combine(root.FullName, ".cache", $"site-api-{Guid.NewGuid():N}.json");
            await using var file = new FileStream(siteConfig, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            await JsonSerializer.SerializeAsync(file, site);
            info.Environment["SVM_SITE_CONFIG_FILE"] = siteConfig;
        }
        string? instanceConfig = null;
        if (instanceAccess is not null)
        {
            instanceConfig = Path.Combine(root.FullName, ".cache", $"instance-api-{Guid.NewGuid():N}.json");
            await using var file = new FileStream(instanceConfig, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            await JsonSerializer.SerializeAsync(file, instanceAccess);
            info.Environment["SVM_INSTANCE_ACCESS_CONFIG_FILE"] = instanceConfig;
        }
        var api = new LocalApi(new Process { StartInfo = info, EnableRaisingEvents = true }, config, cert.Thumbprint, siteConfig, instanceConfig);
        api._publicPort = publicPort;
        if (packages is not null)
        {
            var path = await OutboxFixture.PrivateJsonAsync(packages); api._packageConfigurations.Add(path); info.Environment["SVM_PACKAGE_CONFIG_FILE"] = path;
        }
        if (messaging is not null)
        {
            var path = await OutboxFixture.PrivateJsonAsync(messaging); api._packageConfigurations.Add(path); info.Environment["SVM_MESSAGING_CONFIG_FILE"] = path;
        }
        api._process.OutputDataReceived += (_, e) => api.Capture(e.Data);
        api._process.ErrorDataReceived += (_, e) => api.Capture(e.Data);
        api._process.Exited += (_, _) => api._ready.TrySetException(new InvalidOperationException("Local API exited before readiness; diagnostics retained in test memory."));
        try
        {
            if (!api._process.Start()) throw new InvalidOperationException("Local API failed to start.");
            api._process.BeginOutputReadLine(); api._process.BeginErrorReadLine();
            api.Url = await api._ready.Task.WaitAsync(TimeSpan.FromSeconds(25));
            return api;
        }
        catch (Exception error)
        {
            var diagnostic = string.Join('\n', api._logs);
            foreach (var secret in new[] { certPassword, messaging?.Password, new NpgsqlConnectionStringBuilder(writer ?? database.WriterConnection).Password })
                if (!string.IsNullOrEmpty(secret)) diagnostic = diagnostic.Replace(secret, "[redacted]", StringComparison.Ordinal);
            await api.DisposeAsync();
            if (packages is not null) throw new InvalidOperationException("Package API startup failed: " + diagnostic, error);
            throw;
        }
    }
    private void Capture(string? line)
    {
        if (line is null) return;
        _logs.Enqueue(line);
        const string marker = "Now listening on: ";
        var index = line.IndexOf(marker, StringComparison.Ordinal);
        if (index >= 0)
        {
            var url = line[(index + marker.Length)..].Trim();
            if (_publicPort == 0 || new Uri(url).Port == _publicPort) _ready.TrySetResult(url.Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
        }
    }
    internal HttpClient Client(CookieContainer cookies) => new(new HttpClientHandler
    {
        CookieContainer = cookies, AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate?.Thumbprint == _thumbprint
    }) { Timeout = TimeSpan.FromSeconds(20) };
    internal void AssertRedacted(string secret) => PersistenceDatabase.AssertRedacted(string.Join('\n', _logs), secret);
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        _process.Dispose(); File.Delete(_configuration);
        foreach (var path in _packageConfigurations) File.Delete(path);
        if (_siteConfiguration is not null) File.Delete(_siteConfiguration);
        if (_instanceConfiguration is not null) File.Delete(_instanceConfiguration);
    }
}
