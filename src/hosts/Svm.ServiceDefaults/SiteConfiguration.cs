using System.Text.Json;
using System.Text.Json.Serialization;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Framework;

namespace Svm.ServiceDefaults;

public static class SiteConfiguration
{
    public static SiteCatalogOptions LoadFromEnvironment() => Load(Environment.GetEnvironmentVariable("SVM_SITE_CONFIG_FILE"));
    public static SiteCatalogOptions Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new();
        try
        {
            var value = JsonSerializer.Deserialize<SiteCatalogOptions>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidOperationException();
            value.Validate(); return value;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); }
    }
}
