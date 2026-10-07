using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Svm.EventBus;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Messaging.V1;
using Xunit;

namespace Svm.FrameworkTests;

internal static class OutboxFixture
{
    internal static string Root
    {
        get
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "build/rabbitmq.local.json"))) root = root.Parent;
            return root?.FullName ?? throw new InvalidOperationException("SVM root not found.");
        }
    }
    internal static MessagingOptions Options(Guid? site = null) => new()
    {
        Host = "127.0.0.1", Port = 1, VirtualHost = "svm-test-unreachable", Username = "fixture", Password = "fixture-secret",
        UseTls = false, SiteId = site ?? Guid.NewGuid(), QueryDelaySeconds = 1, MessageDeliveryTimeoutSeconds = 3
    };
    internal static PackageWorkAvailableV1 Message(MessagingOptions options) => new(Guid.NewGuid(), DateTimeOffset.UtcNow,
        Guid.NewGuid(), null, options.SiteId, Guid.NewGuid(), PackageWorkKind.UploadVerificationAndCopy, Guid.NewGuid(), 1);
    internal static ServiceProvider Provider(IdempotencyFixture fixture, MessagingOptions options, params IInterceptor[] interceptors) =>
        fixture.ProviderWithDomainEvents([], new DomainEventOptions(), s => s.AddSvmMessaging(options, delivery: false), interceptors);
    internal static Task<long> MessagesAsync(PersistenceDatabase db) => PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection, "SELECT count(*) FROM framework.\"OutboxMessage\"");
    internal static Task<long> StatesAsync(PersistenceDatabase db) => PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection, "SELECT count(*) FROM framework.\"OutboxState\"");
    internal static async Task<OperationResultReference> StageAsync(IdempotencyFixture fixture, ServiceProvider provider,
        IReadOnlyList<IIntegrationEvent> messages, Guid? key = null) => await fixture.ExecuteAsync(provider, key ?? Guid.NewGuid(), owner: ModuleOwner.Packages,
            action: async (scope, token) =>
            {
                var outbox = scope.ServiceProvider.GetRequiredService<IIntegrationEventOutbox>();
                foreach (var message in messages) await outbox.EnqueueAsync(message, token);
                return await fixture.ApplyAsync(scope, token);
            });
    internal static async Task WaitAsync(Func<Task<bool>> condition, int seconds = 35)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!await condition()) await Task.Delay(100, deadline.Token);
    }
    internal static async Task<string> PrivateJsonAsync(object value)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Project local messaging fixtures require macOS or Linux.");
        var folder = Path.Combine(Root, ".cache/outbox-tests"); Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json");
        await using var file = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        await JsonSerializer.SerializeAsync(file, value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return path;
    }
}

// Owns a random vhost and non-administrator user in this project's labelled test broker only.
public sealed class OutboxBroker : IAsyncLifetime
{
    private readonly HttpClient _admin = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private bool _vhostCreated, _userCreated;
    private string _adminSecret = "";
    internal MessagingOptions Options { get; private set; } = null!;
    internal string Vhost => "svmt_" + _suffix;
    internal string User => "svmt_" + _suffix;
    public async Task InitializeAsync()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("SVM_TEST_RABBITMQ_CONFIG_FILE")
                ?? throw new InvalidOperationException("Use eng/rabbitmq up and SVM_TEST_RABBITMQ_CONFIG_FILE for real RabbitMQ tests.");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path)); var settings = json.RootElement;
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OutboxFixture.Root))).ToLowerInvariant()[..12];
            if (settings.GetProperty("projectIdentity").GetString() != identity || settings.GetProperty("host").GetString() != "127.0.0.1" ||
                settings.GetProperty("container").GetString() != "svm-rabbit-" + identity || settings.GetProperty("username").GetString() != "svm_bootstrap")
                throw new InvalidOperationException("RabbitMQ tests require this project's dedicated broker.");
            _adminSecret = settings.GetProperty("password").GetString()!;
            _admin.BaseAddress = new Uri($"http://127.0.0.1:{settings.GetProperty("managementPort").GetInt32()}/api/");
            _admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("svm_bootstrap:" + _adminSecret)));
            using var overview = await GetAsync("overview"); Assert.Equal("4.3.6", overview.RootElement.GetProperty("rabbitmq_version").GetString());
            await PutAsync("vhosts/" + Vhost, new { description = "SVM owned fixture " + identity, default_queue_type = "quorum", tags = "svm-test" }); _vhostCreated = true;
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await PutAsync("users/" + User, new { password = secret, tags = "" }); _userCreated = true;
            await PutAsync("permissions/" + Vhost + "/" + User, new { configure = ".*", write = ".*", read = ".*" });
            Options = OutboxFixture.Options() with { Port = settings.GetProperty("amqpPort").GetInt32(), VirtualHost = Vhost, Username = User, Password = secret };
        }
        catch { await DisposeAsync(); throw; }
    }
    private async Task PutAsync(string path, object body)
    {
        using var response = await _admin.PutAsJsonAsync(path, body); Assert.True(response.IsSuccessStatusCode, "Owned broker setup failed without printing credentials.");
    }
    internal async Task<JsonDocument> GetAsync(string path)
    {
        using var response = await _admin.GetAsync(path); Assert.True(response.IsSuccessStatusCode, "Owned broker read failed.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    internal async Task<long> QueueCountAsync(string queue = "svm.package-work.available.v1")
    {
        using var response = await _admin.GetAsync("queues/" + Vhost + "/" + queue);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return 0;
        Assert.True(response.IsSuccessStatusCode); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // Management statistics are sampled; wait tests tolerate the sample interval.
        return json.RootElement.TryGetProperty("messages", out var count) ? count.GetInt64() : 0;
    }
    internal async Task<IReadOnlyList<JsonElement>> TakeAsync(string queue = "svm.package-work.available.v1", int count = 100)
    {
        using var response = await _admin.PostAsJsonAsync("queues/" + Vhost + "/" + queue + "/get", new { count, ackmode = "ack_requeue_false", encoding = "auto", truncate = 100000 });
        Assert.True(response.IsSuccessStatusCode); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }
    internal async Task PublishRawAsync(string queue, string payload)
    {
        using var response = await _admin.PostAsJsonAsync("exchanges/"+Vhost+"/"+queue+"/publish",new
        {
            properties=new { content_type="application/vnd.masstransit+json",delivery_mode=2 }, routing_key="", payload, payload_encoding="string"
        });
        Assert.True(response.IsSuccessStatusCode); using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("routed").GetBoolean());
    }
    internal Task StopAsync() => LifecycleAsync("stop");
    internal Task StartAsync() => LifecycleAsync("start");
    private static async Task LifecycleAsync(string command)
    {
        var info = new ProcessStartInfo(Path.Combine(OutboxFixture.Root, "eng/rabbitmq")) { WorkingDirectory = OutboxFixture.Root,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(command); using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(55)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        await output; await errors; Assert.Equal(0, process.ExitCode);
    }
    public async Task DisposeAsync()
    {
        if (_vhostCreated)
        {
            using var response = await _admin.DeleteAsync("vhosts/" + Vhost); Assert.True(response.IsSuccessStatusCode); _vhostCreated = false;
        }
        if (_userCreated)
        {
            using var response = await _admin.DeleteAsync("users/" + User); Assert.True(response.IsSuccessStatusCode); _userCreated = false;
        }
        _admin.Dispose();
    }
}

internal sealed class OutboxWorker : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string[] _paths;
    private readonly Task<string> _stderr;
    private readonly StringBuilder _stdout = new();
    private readonly string _secret;
    private OutboxWorker(Process process, string[] paths, string secret)
    { _process = process; _paths = paths; _secret = secret; _stderr = process.StandardError.ReadToEndAsync(); }
    internal static async Task<OutboxWorker> StartAsync(PersistenceDatabase db, MessagingOptions options)
    {
        var persistence = await OutboxFixture.PrivateJsonAsync(new { writerConnectionString = db.WriterConnection, readerConnectionString = db.ReaderConnection });
        var messaging = await OutboxFixture.PrivateJsonAsync(options);
        var info = new ProcessStartInfo(Path.Combine(OutboxFixture.Root, "eng/dotnet")) { WorkingDirectory = OutboxFixture.Root,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(Path.Combine(OutboxFixture.Root, "src/hosts/Svm.Worker/bin/Debug/net8.0/Svm.Worker.dll"));
        info.Environment["SVM_PERSISTENCE_CONFIG_FILE"] = persistence; info.Environment["SVM_MESSAGING_CONFIG_FILE"] = messaging;
        info.Environment["Logging__LogLevel__Default"] = "Information"; info.Environment["Logging__LogLevel__MassTransit"] = "Warning";
        var process = new Process { StartInfo = info }; var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OutboxWorker? worker = null;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (worker!._stdout) worker._stdout.AppendLine(e.Data);
            if (e.Data.Contains("Application started.", StringComparison.Ordinal)) ready.TrySetResult();
        };
        try
        {
            Assert.True(process.Start()); worker = new OutboxWorker(process, [persistence, messaging], options.Password); process.BeginOutputReadLine();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15)); Assert.False(process.HasExited); return worker;
        }
        catch
        {
            if (worker is not null) await worker.DisposeAsync();
            else { process.Dispose(); File.Delete(persistence); File.Delete(messaging); }
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        var errors = await _stderr;
        foreach (var path in _paths) File.Delete(path);
        lock (_stdout) PersistenceDatabase.AssertRedacted(_stdout.ToString(), _secret);
        PersistenceDatabase.AssertRedacted(errors, _secret); _process.Dispose();
        await File.WriteAllTextAsync(Path.Combine(OutboxFixture.Root, "artifacts", "outbox-worker-" + Guid.NewGuid().ToString("N") + ".log"), _stdout.ToString() + errors);
    }
}
