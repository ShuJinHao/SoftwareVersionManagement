using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;

namespace Svm.FileStorage;

/// <summary>Append-only generation segments survive worker restart; cursor advances only after confirmed facts.</summary>
internal sealed class PackageDownloadLogPump(PackageFileOptions options, IUnitOfWork unit) : IPackageDownloadLogPump, IDisposable
{
    private readonly PackagePeerClient _collector = new(options, collector: true);
    private sealed record LogRecord(string RequestId, string NodeId, string WorkerGeneration, string EndedAt, string BytesSent, string Outcome, string Authorized);
    public async Task PumpAsync(CancellationToken token)
    {
        if (unit.CurrentOperationId is not null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting);
        var directory = options.DownloadLogDirectory;
        if (!Directory.Exists(directory)) return;
        if (new DirectoryInfo(directory).LinkTarget is not null) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var cursorDirectory = Path.Combine(options.RootPath, "download-cursors"); Directory.CreateDirectory(cursorDirectory);
        if (new DirectoryInfo(cursorDirectory).LinkTarget is not null) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        foreach (var segment in Directory.EnumerateFiles(directory, "*.jsonl").Order(StringComparer.Ordinal))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(segment), "D", out var generation) || new FileInfo(segment).LinkTarget is not null)
                throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
            FileStream mutex;
            try { mutex = new(Path.Combine(cursorDirectory, generation.ToString("D") + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { continue; }
            await using (mutex)
            {
                var cursor = Path.Combine(cursorDirectory, generation.ToString("D") + ".offset");
                var position = File.Exists(cursor) ? long.Parse(await File.ReadAllTextAsync(cursor, token), CultureInfo.InvariantCulture) : 0;
                await using var file = new FileStream(segment, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.Asynchronous);
                if (file.Length < position || position < 0) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
                file.Position = position;
                for (var count = 0; count < 100; count++)
                {
                    var line = await Line(file, token); if (line is null) break;
                    var record = JsonSerializer.Deserialize<LogRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new RequestRejectedException(RequestFailure.InvalidRequest);
                    if (record.Authorized == "1")
                    {
                        if (record.NodeId != options.NodeId || !Guid.TryParseExact(record.WorkerGeneration, "D", out var reportedGeneration) || reportedGeneration != generation ||
                            !Guid.TryParse(record.RequestId, out var request) || request == Guid.Empty ||
                            !decimal.TryParse(record.EndedAt, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
                            !long.TryParse(record.BytesSent, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)) throw new RequestRejectedException(RequestFailure.InvalidRequest);
                        var end = new DownloadEnd(request, options.NodeId, generation, DateTimeOffset.UnixEpoch.AddTicks(checked((long)(seconds * TimeSpan.TicksPerSecond))), bytes, record.Outcome);
                        var unresolved = Path.Combine(cursorDirectory, request.ToString("D") + ".unknown");
                        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(30));
                        // Read first even after a crash between the POST response and cursor persistence.
                        var uri = Address($"internal/v1/download-completions/{request:D}?nodeId={options.NodeId}&workerGeneration={generation:D}");
                        using var check = await _collector.Client.GetAsync(uri, budget.Token);
                        if (!check.IsSuccessStatusCode) break;
                        var fact = check.StatusCode == System.Net.HttpStatusCode.NoContent ? null :
                            await check.Content.ReadFromJsonAsync<DownloadEnd>(cancellationToken: budget.Token);
                        if (fact is not null)
                        {
                            if (fact != end) break; File.Delete(unresolved);
                        }
                        else
                        {
                            // Persist query-only intent BEFORE sending. A transport exception or process exit
                            // cannot turn an unknown commit into an automatic Handler re-execution.
                            if (File.Exists(unresolved)) break;
                            await PrivateWrite(unresolved, "query-only", token);
                            using var response = await _collector.Client.PostAsJsonAsync(Address("internal/v1/download-completions"), end, budget.Token);
                            if (!response.IsSuccessStatusCode)
                            {
                                if ((int)response.StatusCode >= 500)
                                {
                                    using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(budget.Token));
                                    if (problem.RootElement.TryGetProperty("retryable", out var retry) && retry.ValueKind == JsonValueKind.True)
                                        File.Delete(unresolved); // Server explicitly confirmed rollback.
                                }
                                break;
                            }
                            File.Delete(unresolved);
                        }
                    }
                    position = file.Position; await PrivateWrite(cursor, position.ToString(CultureInfo.InvariantCulture), token);
                }
            }
        }
    }
    private Uri Address(string path) => new(new Uri(options.Nodes.Single(n => n.NodeId == options.NodeId).InternalBaseUri), path);
    private static async Task<string?> Line(FileStream stream, CancellationToken token)
    {
        var bytes = new List<byte>(512); var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, token) > 0)
        {
            if (buffer[0] == 10) return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(buffer[0]); if (bytes.Count > 8192) throw new RequestRejectedException(RequestFailure.PayloadTooLarge);
        }
        return null; // The incomplete append is retried from the last persisted cursor.
    }
    private static async Task PrivateWrite(string path, string value, CancellationToken token)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var temp = path + "." + Guid.NewGuid().ToString("N");
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var stream = new FileStream(temp, fileOptions))
        { await stream.WriteAsync(Encoding.UTF8.GetBytes(value), token); await stream.FlushAsync(token); stream.Flush(true); }
        File.Move(temp, path, overwrite: true);
    }
    public void Dispose() => _collector.Dispose();
}
