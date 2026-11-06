using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Svm.Services.Contracts.Instances;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Tasks;

namespace Svm.ServiceDefaults;

public sealed record AcceptanceSettings
{
    public required string SiteName { get; init; }
    public required string TimeZone { get; init; }
    public required string PublicHost { get; init; }
    public required string PublicBind { get; init; }
    public required int HttpsPort { get; init; }
    public required int SecondaryHttpsPort { get; init; }
    public required string DataDirectory { get; init; }
    public required int ServiceUid { get; init; }
    public required int ServiceGid { get; init; }
    public required string NetworkSubnet { get; init; }
    public required string GatewayAddress { get; init; }
    public required string EmployeeNo { get; init; }
    public required string AdministratorName { get; init; }
    public required string CertificateMode { get; init; }
    public required int CertificateDays { get; init; }
    public required InstanceAccessOptions Instances { get; init; }
    public required PackageLimits PackageLimits { get; init; }
    public required PackageExecutionOptions PackageExecution { get; init; }
    public required TaskOptions Task { get; init; }
    public required int PlcUploadCredentialMaxLifetimeSeconds { get; init; }
    public override string ToString() => "Acceptance settings [private input omitted]";

    public static async Task<AcceptanceSettings> LoadAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > 32768 || new FileInfo(path).LinkTarget is not null) throw new IOException("Invalid settings file.");
        var settings = JsonSerializer.Deserialize<AcceptanceSettings>(await File.ReadAllTextAsync(path, token), CandidateBundle.Json) ?? throw new IOException();
        settings.Validate(); return settings;
    }
    public void Validate()
    {
        static bool Name(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && value == value.Trim() && !value.Any(char.IsControl) && !value.Contains('<');
        if (!Name(SiteName, 200) || !Name(AdministratorName, 100) || !Regex.IsMatch(EmployeeNo ?? "", "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$") ||
            PublicHost is null || !Regex.IsMatch(PublicHost, "^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$") || PublicHost.EndsWith('.') || PublicHost.Contains("..", StringComparison.Ordinal) ||
            !IPAddress.TryParse(PublicBind, out var bind) || bind.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            HttpsPort is < 1024 or > 65535 || SecondaryHttpsPort is < 1024 or > 65535 || HttpsPort == SecondaryHttpsPort ||
            ServiceUid is < 1 or > 2147483646 || ServiceGid is < 1 or > 2147483646 || CertificateMode != "acceptance-test" || CertificateDays is < 1 or > 90 ||
            PlcUploadCredentialMaxLifetimeSeconds is < 1 or > 31536000 ||
            string.IsNullOrWhiteSpace(DataDirectory) || !DataDirectory.StartsWith('/') || DataDirectory.Length > 512 ||
            DataDirectory.Any(c => char.IsControl(c) || "\\\"'$`,:;{}<>".Contains(c)) || DataDirectory.Split('/').Skip(1).Any(p => p is "" or "." or ".."))
            throw new IOException("Invalid explicit acceptance settings.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        var subnet = (NetworkSubnet ?? "").Split('/');
        if (subnet.Length != 2 || !int.TryParse(subnet[1], out var prefix) || prefix is < 24 or > 29 ||
            !IPAddress.TryParse(subnet[0], out var network) || network.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !IPAddress.TryParse(GatewayAddress, out var gateway) || gateway.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new IOException("Invalid deployment subnet.");
        static uint Address(IPAddress ip) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        var mask = uint.MaxValue << (32 - prefix); var first = Address(network); var last = first | ~mask; var host = Address(gateway);
        if ((first & mask) != first || (host & mask) != first || host <= first + 1 || host >= last) throw new IOException("Gateway must be in the subnet, distinct from Docker's gateway.");
        if (Instances is null || PackageLimits is null || PackageExecution is null || Task is null) throw new IOException("Explicit feature parameters are required.");
        Instances.Validate(); PackageLimits.Validate(); PackageExecution.Validate(); Task.Validate();
    }
}
