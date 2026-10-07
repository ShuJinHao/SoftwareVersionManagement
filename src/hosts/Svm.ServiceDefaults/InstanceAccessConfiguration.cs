using System.Text.Json;
using System.Text.Json.Serialization;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Instances;

namespace Svm.ServiceDefaults;

public static class InstanceAccessConfiguration
{
    public static InstanceAccessOptions LoadFromEnvironment() => Load(Environment.GetEnvironmentVariable("SVM_INSTANCE_ACCESS_CONFIG_FILE"));
    public static InstanceAccessOptions Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new();
        try
        {
            var options = JsonSerializer.Deserialize<InstanceAccessOptions>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidOperationException();
            options.Validate();
            return options;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); }
    }
}
