using System.Text.Json;
using System.Text.Json.Serialization;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Tasks;

namespace Svm.ServiceDefaults;

public static class TaskConfiguration
{
    public static TaskOptions? LoadFromEnvironment() => Load(Environment.GetEnvironmentVariable("SVM_TASK_CONFIG_FILE"));
    public static TaskOptions? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        { var result = JsonSerializer.Deserialize<TaskOptions>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidOperationException(); result.Validate(); return result; }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); }
    }
}
