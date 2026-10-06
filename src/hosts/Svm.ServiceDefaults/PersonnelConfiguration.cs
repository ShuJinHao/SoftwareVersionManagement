using System.Text.Json;
using System.Text.Json.Serialization;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;

namespace Svm.ServiceDefaults;

public sealed class PersonnelConfiguration
{
    public string ApplicationName { get; init; } = "";
    public string CertificatePath { get; init; } = "";
    public string CertificatePassword { get; init; } = "";
    public PersonnelPolicy Policy { get; init; } = null!;
    public override string ToString() => "Personnel configuration [redacted]";
    public static PersonnelConfiguration LoadFromEnvironment() => Load(Environment.GetEnvironmentVariable("SVM_PERSONNEL_CONFIG_FILE"));
    public static PersonnelConfiguration Load(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException();
            var value = JsonSerializer.Deserialize<PersonnelConfiguration>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidOperationException();
            if (!value.ApplicationName.StartsWith("svm/", StringComparison.Ordinal) || value.ApplicationName.Length > 100 ||
                !Path.IsPathFullyQualified(value.CertificatePath) || !File.Exists(value.CertificatePath) || value.CertificatePassword.Length < 16 || value.Policy is null)
                throw new InvalidOperationException();
            value.Policy.Validate();
            return value;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); }
    }
}
