using System.Text.Json;
using System.Text.Json.Serialization;
using Svm.Services.Contracts.Framework;

namespace Svm.EventBus;

public static class MessagingConfiguration
{
    public static MessagingOptions? LoadFromEnvironment() => Load(Environment.GetEnvironmentVariable("SVM_MESSAGING_CONFIG_FILE"));

    public static MessagingOptions? Load(string? path)
    {
        if (path is null) return null;
        try
        {
            if (string.IsNullOrWhiteSpace(path)) throw new OutboxException(OutboxFailure.ConfigurationInvalid);
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) &
                (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
                throw new OutboxException(OutboxFailure.ConfigurationInvalid);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                json.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != json.RootElement.EnumerateObject().Count())
                throw new OutboxException(OutboxFailure.ConfigurationInvalid);
            var options = json.RootElement.Deserialize<MessagingOptions>(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            }) ?? throw new OutboxException(OutboxFailure.ConfigurationInvalid);
            options.Validate(); return options;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { throw new OutboxException(OutboxFailure.ConfigurationInvalid); }
    }
}
