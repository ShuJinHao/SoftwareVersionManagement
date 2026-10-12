using Microsoft.Extensions.DependencyInjection;
using Svm.Core.Releases;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;

namespace Svm.ReleaseService;

public static class ReleaseRegistration
{
    public static IServiceCollection AddSvmReleases(this IServiceCollection services) => services.AddScoped<IReleases, Releases>();
}
internal sealed class Releases(IReleaseRepository repository, ISoftwareCatalogRepository software,
    IUnitOfWork unit, TimeProvider clock) : IReleases
{
    public async Task<Guid?> SoftwareForAsync(Guid id, bool protect, CancellationToken token) => (await repository.GetAsync(id, protect, token))?.SoftwareId;
    public async Task<ReleaseView> GetAsync(Guid id, bool protect, CancellationToken token) => View(await Existing(id, protect, token));
    public async Task<ReleaseView> CreateAsync(CreateReleaseCommand input, Guid packageId, Guid actorId, CancellationToken token)
    {
        RequireWrite();
        if (await software.GetAsync(input.SoftwareId, true, token) is null) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        var number = SoftwareRelease.Next(await repository.LatestAsync(input.SoftwareId, token), input.ChangeLevel);
        if (input.ExpectedVersion is { } expected && expected != $"{number.Major}.{number.Minor}.{number.Patch}")
            throw new RequestRejectedException(RequestFailure.VersionConflict);
        var release = new SoftwareRelease(Guid.NewGuid(), input.SoftwareId, number.Major, number.Minor, number.Patch,
            input.ChangeLevel, input.ChangeSummary, input.ChangeReason, packageId, actorId, clock.GetUtcNow());
        repository.Add(release); return View(release);
    }
    public async Task<ReleaseView> DisableAsync(Guid id, long revision, string reason, CancellationToken token)
    {
        RequireWrite(); var release = await Existing(id, true, token);
        if (release.Revision != revision) throw new RequestRejectedException(RequestFailure.RevisionConflict);
        if (release.State == "Disabled") throw new RequestRejectedException(RequestFailure.InvalidState);
        release.Disable(reason, clock.GetUtcNow()); return View(release);
    }
    public async Task<ReleaseView> PublishAsync(PublishReleaseCommand input, Guid actorId, string employeeNo, CancellationToken token)
    {
        RequireWrite(); var release = await Existing(input.ReleaseId, true, token);
        if (release.Revision != input.ExpectedRevision) throw new RequestRejectedException(RequestFailure.RevisionConflict);
        if (release.State != "Test" || release.PublishedAt is not null) throw new RequestRejectedException(RequestFailure.InvalidState);
        release.Publish(input.TestEvidenceId, input.PublishReason, input.PublishConclusion, actorId, employeeNo, clock.GetUtcNow());
        return View(release);
    }
    public async Task OpenTestAsync(Guid id, Guid packageId, CancellationToken token)
    {
        RequireWrite(); var release = await Existing(id, true, token);
        if (release.PackageId != packageId || release.State is not ("Staging" or "Test")) throw new RequestRejectedException(RequestFailure.InvalidState);
        release.OpenTest();
    }
    public async Task VerifyInstallationAsync(Guid softwareId, Guid releaseId, string? version, CancellationToken token)
    {
        var release = await Existing(releaseId, false, token);
        if (release.SoftwareId != softwareId) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        if (release.Version != version) throw new RequestRejectedException(RequestFailure.ValidationFailed);
    }
    private async Task<SoftwareRelease> Existing(Guid id, bool protect, CancellationToken token) =>
        await repository.GetAsync(id, protect, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
    private void RequireWrite() { if (unit.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
    private static ReleaseView View(SoftwareRelease r) => new(r.Id.Value, r.SoftwareId, r.Version, r.State, r.ChangeLevel,
        r.ChangeSummary, r.ChangeReason, r.PackageId, false, r.CreatedBy, r.CreatedAt, r.DisabledAt, r.DisableReason, r.Revision,
        r.PublishedBy, r.PublishedEmployeeNo, r.PublishedAt, r.TestEvidenceId, r.PublishReason, r.PublishConclusion);
}
