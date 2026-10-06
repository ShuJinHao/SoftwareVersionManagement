using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Svm.Application;
using Svm.AuditService;
using Svm.EntityFrameworkCore;
using Svm.IdentityService;
using Svm.Security;
using Svm.ServiceDefaults;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.CrossCutting.Registration;

namespace Svm.Migration;

internal sealed class SeedConfiguration
{
    public string WriterConnectionString { get; init; } = "";
    public string PersonnelConfigurationFile { get; init; } = "";
    public string EmployeeNo { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string TemporaryPassword { get; init; } = "";
    public override string ToString() => "Seed configuration [redacted]";
}
internal sealed class SeedContext : ITrustedCallContextSource, ISessionProofSource
{
    private readonly CallContextSnapshot _context = new(new CallActor(ActorKind.Service,
        Guid.Parse("b0b90b26-1842-45a4-823d-e8c321f259e2"), workOwner: ModuleOwner.Identity, workId: Guid.NewGuid()), RequestKind.Internal, Guid.NewGuid().ToString("N"));
    public CallContextSnapshot GetCurrent() => _context;
    public SessionProof? Proof => null;
    public string SourceAddress => "explicit-local-seed";
}
internal static class PersonnelSeed
{
    internal static async Task<bool> ExecuteAsync(string path, CancellationToken cancellationToken)
    {
        SeedConfiguration config;
        try
        {
            config = JsonSerializer.Deserialize<SeedConfiguration>(await File.ReadAllTextAsync(path, cancellationToken),
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new JsonException();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); }
        var policy = PersonnelConfiguration.Load(config.PersonnelConfigurationFile).Policy;
        var services = new ServiceCollection();
        services.AddSvmSeedApplication().AddSvmPostgres(config.WriterConnectionString).AddSvmPersonnel().AddSvmAudit().AddSvmPersonnelCrypto(policy);
        services.AddScoped<SeedContext>();
        services.AddScoped<ITrustedCallContextSource>(p => p.GetRequiredService<SeedContext>());
        services.AddScoped<ISessionProofSource>(p => p.GetRequiredService<SeedContext>());
        services.ValidateSvmFoundation();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(
            new SeedPersonnelCommand(config.EmployeeNo, config.DisplayName, config.TemporaryPassword), cancellationToken);
        return result.Value.Changed;
    }
}
