using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Svm.Services.Contracts.Framework;

namespace Svm.ServiceDefaults;

public sealed record HostRuntimeConfiguration(string? PublicCertificatePath = null, string? PublicCertificatePassword = null,
    string[]? KnownProxies = null, int? HealthPort = null)
{
    public override string ToString() => "Host runtime configuration [redacted]";
    public static HostRuntimeConfiguration Load()
    {
        var path = Environment.GetEnvironmentVariable("SVM_HOST_CONFIG_FILE");
        if (string.IsNullOrWhiteSpace(path)) return new();
        try
        {
            var result = JsonSerializer.Deserialize<HostRuntimeConfiguration>(File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
                ?? throw new InvalidOperationException();
            result.Validate(); return result;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); }
    }
    public void Validate()
    {
        if (HealthPort is not null && HealthPort is < 1024 or > 65535 ||
            PublicCertificatePath is not null && (!Path.IsPathFullyQualified(PublicCertificatePath) || !File.Exists(PublicCertificatePath)) ||
            PublicCertificatePath is null != (PublicCertificatePassword is null) ||
            KnownProxies is { Length: > 16 } || KnownProxies?.Any(x => !IPAddress.TryParse(x, out var ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) == true)
            throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    }
    public ForwardedHeadersOptions Forwarding()
    {
        Validate();
        var options = new ForwardedHeadersOptions { ForwardLimit = 1, ForwardedHeaders = ForwardedHeaders.XForwardedFor };
        options.KnownNetworks.Clear(); options.KnownProxies.Clear();
        foreach (var address in KnownProxies ?? []) options.KnownProxies.Add(IPAddress.Parse(address));
        // An empty list disables forwarding; it never means trust every peer.
        if (options.KnownProxies.Count == 0) options.ForwardedHeaders = ForwardedHeaders.None;
        return options;
    }
}
