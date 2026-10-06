using System.Text.Json;
using Svm.Services.Contracts.Framework;

namespace Svm.ServiceDefaults;

/// <summary>Explicit host configuration; contains only runtime roles, never migration/admin credentials.</summary>
public sealed class PersistenceConfiguration
{
    private PersistenceConfiguration(string writer, string reader) { WriterConnectionString = writer; ReaderConnectionString = reader; }
    public string WriterConnectionString { get; }
    public string ReaderConnectionString { get; }
    public override string ToString() => "Runtime persistence configuration (redacted)";

    public static PersistenceConfiguration LoadFromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("SVM_PERSISTENCE_CONFIG_FILE");
        if (string.IsNullOrWhiteSpace(path)) throw new PersistenceException(PersistenceFailure.ConfigurationInvalid);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var json = document.RootElement;
            var names = json.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length != 2 || names.Distinct().Count() != 2 || names.Except(["writerConnectionString", "readerConnectionString"]).Any())
                throw new PersistenceException(PersistenceFailure.ConfigurationInvalid);
            var writer = json.GetProperty("writerConnectionString").GetString();
            var reader = json.GetProperty("readerConnectionString").GetString();
            if (string.IsNullOrWhiteSpace(writer) || string.IsNullOrWhiteSpace(reader) || writer == reader)
                throw new PersistenceException(PersistenceFailure.ConfigurationInvalid);
            return new(writer, reader);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { throw new PersistenceException(PersistenceFailure.ConfigurationInvalid); }
    }
}
