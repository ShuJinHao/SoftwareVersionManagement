using Svm.Core.Releases;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Releases;

namespace Svm.ReleaseService;

internal sealed class ReleaseImports(IReleaseImportRepository repository, IReleaseRepository releases,
    ISoftwareCatalogRepository software, IUnitOfWork unit, GitLabImportOptions options, TimeProvider clock) : IReleaseImports
{
    public async Task<bool> IsBoundAsync(Guid id, CancellationToken token) => options.Bindings.Any(b => b.SoftwareId == id) || await repository.BindingAsync(id, false, token) is not null;
    public async Task<bool> AuthorizesPackageAsync(Guid releaseId, Guid subject, Guid softwareId, CancellationToken token)
    {
        var release = await releases.GetAsync(releaseId, false, token);
        if (release is null || release.SoftwareId != softwareId || release.SourceKind != "GitLab" || release.SourceBindingId is not { } bindingId) return false;
        var stored = await repository.BindingByIdAsync(bindingId, false, token);
        return stored is not null && stored.SoftwareId == softwareId && stored.SubjectId == subject && release.CreatedBy == subject;
    }
    public async Task<ReleaseSourceView> SourceAsync(Guid id, CancellationToken token)
    {
        var stored = await repository.BindingAsync(id, false, token);
        if (stored is not null) return View(stored) with { BlockingWorkIds = await repository.BlockingWorkIdsAsync(stored.Id, token) };
        var configured = options.Bindings.SingleOrDefault(b => b.SoftwareId == id);
        return new(id, configured is null ? "Manual" : "GitLab", configured?.Enabled == true, configured?.BaseUri,
            configured?.ProjectId, configured?.PackageName, null, null, null, configured?.Id,
            ConfigurationAvailable: configured is not null);
    }
    public async Task<GitLabBinding?> ResolveBindingAsync(Guid softwareId, Guid? bindingId, bool protect, CancellationToken token)
    {
        if (protect)
        {
            RequireWrite();
            if (await software.GetAsync(softwareId, true, token) is null) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        }
        var stored = bindingId is { } id ? await repository.BindingByIdAsync(id, protect, token) : await repository.BindingAsync(softwareId, protect, token);
        if (stored is not null) return stored.SoftwareId == softwareId ? ToBinding(stored) : null;
        var configured = options.Bindings.SingleOrDefault(b => b.SoftwareId == softwareId && (bindingId is null || b.Id == bindingId));
        if (configured is not null && protect) repository.Add(new ReleaseSourceBinding(configured.Id, configured.SoftwareId, configured.SubjectId, configured.BaseUri, configured.ProjectId, configured.PackageName));
        return configured;
    }
    public async Task<IReadOnlyList<GitLabBinding>> BindingsAsync(Guid? afterId, int count, CancellationToken token)
    {
        if (count is < 1 or > 200) throw new RequestRejectedException(RequestFailure.ValidationFailed);
        var rows = await repository.BindingsAsync(afterId, count, token);
        return rows.Select(ToBinding).ToArray();
    }
    public async Task<ReleaseSourceView> ChangeSourceAsync(Guid softwareId, Guid connectionId, long projectId, string packageName, bool enabled, long revision, CancellationToken token)
    {
        RequireWrite();
        if (await software.GetAsync(softwareId, true, token) is null) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        var current = await repository.BindingAsync(softwareId, true, token);
        var option = !enabled && current is { Managed: true } && current.ConnectionId == connectionId && current.ProjectId == projectId && current.PackageName == packageName
            ? new GitLabSourceOption(connectionId, "", current.BaseUri, projectId, "", packageName)
            : options.Source(connectionId, projectId, packageName);
        if ((current?.Revision ?? 0) != revision) throw new RequestRejectedException(RequestFailure.RevisionConflict);
        var same = current is not null && current.BaseUri == option.BaseUri && current.ProjectId == projectId && current.PackageName == packageName &&
            (!current.Managed || current.ConnectionId == connectionId);
        if (current is not null && (!same || !current.Managed))
        {
            var blocking = await repository.BlockingWorkIdsAsync(current.Id, token);
            if (blocking.Count != 0) throw new RequestRejectedException(RequestFailure.InvalidState,
                blocking.Select(id => new ValidationIssue("workId", "WORK_IN_PROGRESS", id.ToString("D"))));
        }
        if (same)
        {
            if (current!.Managed) current.SetEnabled(enabled);
            else current.Manage(connectionId, enabled, revision + 1);
        }
        else
        {
            current?.Retire();
            var legacy = current is null ? options.Bindings.SingleOrDefault(b => b.SoftwareId == softwareId && b.BaseUri == option.BaseUri && b.ProjectId == projectId && b.PackageName == packageName) : null;
            current = new(legacy?.Id ?? Guid.NewGuid(), softwareId, legacy?.SubjectId ?? Guid.NewGuid(), option.BaseUri, projectId, packageName);
            current.Manage(connectionId, enabled, revision + 1); repository.Add(current);
        }
        var change = new ReleaseSourceChange(Guid.NewGuid(), current!.Id, current.Revision, current.Enabled, clock.GetUtcNow()); repository.Add(change);
        return View(current) with { ChangeId = change.Id };
    }
    public async Task<ReleaseSourceView> ChangeResultAsync(Guid changeId, CancellationToken token)
    {
        var change = await repository.ChangeAsync(changeId, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        var binding = await repository.BindingByIdAsync(change.BindingId, false, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        return View(binding) with { ChangeId = change.Id, Revision = change.Revision, Enabled = change.Enabled };
    }
    public async Task<ReleaseSyncWorkView> ViewAsync(Guid id, CancellationToken token) => View(await Existing(id, false, token));
    public async Task<GitLabWork> WorkAsync(Guid id, bool protect, CancellationToken token) => Work(await Existing(id, protect, token));
    public async Task<ReleaseSyncWorkView> ScheduleAsync(Guid id, string? tag, bool automatic, CancellationToken token)
    {
        var binding = await Binding(id, token);
        if (!ToBinding(binding).Enabled) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        var now = clock.GetUtcNow();
        var existing = await repository.FindAsync(binding.Id, tag, tag is null, token);
        if (existing is not null)
        { if (!automatic && tag is not null) { existing.Retry(now); binding.Track(existing.Id); } return View(existing); }
        if (automatic && binding.NextScanAt > now && binding.LatestWorkId is { } latest) return View(await Existing(latest, false, token));
        var work = new ReleaseSyncWork(Guid.NewGuid(), binding.Id, id, tag, tag is null && !binding.Initialized, now);
        repository.Add(work); binding.Scheduled(work.Id, now.AddSeconds(options.PollSeconds)); return View(work);
    }
    public Task<IReadOnlyList<Guid>> PendingAsync(Guid id, CancellationToken token) => repository.PendingAsync(id, clock.GetUtcNow(), token);
    public async Task<GitLabWork?> ClaimAsync(Guid id, Guid leaseToken, CancellationToken token)
    {
        var known = await Existing(id, false, token); await WorkBinding(known, token);
        var work = await Existing(id, true, token); return work.Claim(leaseToken, clock.GetUtcNow(), options.LeaseSeconds) ? Work(work) : null;
    }
    public async Task<GitLabWork> ProtectAsync(GitLabLease lease, CancellationToken token)
    {
        var known = await Existing(lease.WorkId, false, token); await WorkBinding(known, token);
        var work = await Existing(lease.WorkId, true, token);
        if (!work.Owns(lease.Token, lease.Generation, clock.GetUtcNow())) throw new RequestRejectedException(RequestFailure.InvalidState);
        work.Renew(clock.GetUtcNow().AddSeconds(options.LeaseSeconds)); return Work(work);
    }
    public async Task ScanAsync(GitLabLease lease, IReadOnlyList<GitLabReleaseFacts> facts, IReadOnlyList<string> tags, IReadOnlyList<string> pendingTags, int? next, CancellationToken token)
    {
        await ProtectAsync(lease, token); var work = await Existing(lease.WorkId, true, token);
        if (work.Kind != "Scan" || next is { } page && (page != work.Page + 1 || page > options.MaxPages)) throw new RequestRejectedException(RequestFailure.InvalidState);
        var binding = await WorkBinding(work, token); var candidate = GitLabFacts.Decode(work.CandidateJson);
        foreach (var fact in facts)
        {
            ValidateFact(ToBinding(binding), fact);
            if (work.InitialScan)
            { if (candidate is null || GitLabVersions.Compare(fact.Manifest.Version, candidate.Manifest.Version) > 0) candidate = fact; }
        }
        foreach (var tag in tags)
        {
            if (!work.InitialScan) await DiscoverTag(binding, tag, token);
            // A pipeline still in progress must remain discoverable after bootstrap initializes the binding.
            else if (pendingTags.Contains(tag, StringComparer.Ordinal)) continue;
            else if (await repository.FindAsync(binding.Id, tag, false, token) is null)
            {
                var observed = new ReleaseSyncWork(Guid.NewGuid(), binding.Id, binding.SoftwareId, tag, false, clock.GetUtcNow());
                observed.ObserveHistory(); repository.Add(observed);
            }
        }
        work.Scan(next ?? work.Page, candidate is null ? null : GitLabFacts.Encode(candidate));
        if (next is null)
        {
            if (work.InitialScan && candidate is not null) await Discover(binding, candidate, token);
            binding.Scanned(clock.GetUtcNow(), !work.InitialScan || candidate is not null);
            work.Complete(clock.GetUtcNow());
        }
        else work.Delay("SCAN_CONTINUES", clock.GetUtcNow(), false);
    }
    private async Task Discover(ReleaseSourceBinding binding, GitLabReleaseFacts fact, CancellationToken token)
        => await DiscoverTag(binding, fact.Manifest.Tag, token, includeHistory: true);
    private async Task DiscoverTag(ReleaseSourceBinding binding, string tag, CancellationToken token, bool includeHistory = false)
    {
        var existing = await repository.FindAsync(binding.Id, tag, false, token);
        if (existing is not null) { if (includeHistory || existing.Stage != "Historical") { existing.Retry(clock.GetUtcNow()); binding.Track(existing.Id); } return; }
        var work = new ReleaseSyncWork(Guid.NewGuid(), binding.Id, binding.SoftwareId, tag, false, clock.GetUtcNow());
        repository.Add(work); binding.Track(work.Id);
    }
    public async Task<ReleaseView> RegisterAsync(GitLabLease lease, GitLabReleaseFacts facts, Guid packageId, CancellationToken token)
    {
        await ProtectAsync(lease, token); var work = await Existing(lease.WorkId, true, token);
        var config = ToBinding(await WorkBinding(work, token)); ValidateFact(config, facts);
        if (work.Kind != "Import" || work.Tag != facts.Manifest.Tag) throw new RequestRejectedException(RequestFailure.InvalidState);
        var existing = await repository.TagAsync(config.Id, work.Tag, token);
        if (existing is not null)
        {
            if (!GitLabFacts.Same(GitLabFacts.Decode(existing.SourceFactsJson)!, facts)) throw new RequestRejectedException(RequestFailure.VersionConflict);
            work.Registered(existing.Id.Value, existing.SourceFactsJson!); return Releases.View(existing);
        }
        var numbers = facts.Manifest.Version.Split('.').Select(int.Parse).ToArray();
        if (await repository.VersionAsync(work.SoftwareId, numbers[0], numbers[1], numbers[2], token) is not null) throw new RequestRejectedException(RequestFailure.VersionConflict);
        var release = new SoftwareRelease(Guid.NewGuid(), work.SoftwareId, numbers[0], numbers[1], numbers[2], "GitLab", facts.ReleaseName,
            "GitLab protected tag and successful pipeline", packageId, config.SubjectId, clock.GetUtcNow());
        release.SetGitLabSource(config.Id, work.Tag!, GitLabFacts.Encode(facts)); releases.Add(release);
        work.Registered(release.Id.Value, release.SourceFactsJson!); return Releases.View(release);
    }
    public async Task SetReceiveAsync(GitLabLease lease, Guid receiveToken, CancellationToken token)
    { await ProtectAsync(lease, token); (await Existing(lease.WorkId, true, token)).Receiving(receiveToken); }
    public async Task ReceivedAsync(GitLabLease lease, CancellationToken token)
    { await ProtectAsync(lease, token); (await Existing(lease.WorkId, true, token)).Received(clock.GetUtcNow().AddSeconds(options.PollSeconds)); }
    public async Task DelayAsync(GitLabLease lease, string code, CancellationToken token)
    {
        await ProtectAsync(lease, token); var work = await Existing(lease.WorkId, true, token);
        (await WorkBinding(work, token)).Error(code);
        work.Delay(code, clock.GetUtcNow().AddSeconds(options.PollSeconds), code == "VERSION_CONFLICT");
    }
    public async Task CompleteReleaseAsync(Guid id, Guid packageId, CancellationToken token)
    {
        RequireWrite(); var release = await releases.GetAsync(id, true, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        if (release.SourceKind != "GitLab" || release.PackageId != packageId || release.State == "Disabled") throw new RequestRejectedException(RequestFailure.InvalidState);
        if (await software.GetAsync(release.SoftwareId, true, token) is null) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        var binding = release.SourceBindingId is { } sourceId ? await repository.BindingByIdAsync(sourceId, true, token) : null;
        if (binding is null || binding.Id != release.SourceBindingId || binding.SubjectId != release.CreatedBy)
            throw new RequestRejectedException(RequestFailure.InvalidState);
        if (release.State == "Staging") release.OpenImportedFormal(clock.GetUtcNow());
        var work = await repository.FindAsync(release.SourceBindingId!.Value, release.SourceTag, false, token);
        work?.Complete(clock.GetUtcNow());
        binding.Scanned(clock.GetUtcNow(), false);
    }
    private async Task<ReleaseSourceBinding> Binding(Guid id, CancellationToken token)
    {
        var resolved = await ResolveBindingAsync(id, null, true, token);
        if (resolved is null || !options.IsAvailable(resolved)) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        return await repository.BindingByIdAsync(resolved.Id, true, token) ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    }
    private async Task<ReleaseSourceBinding> WorkBinding(ReleaseSyncWork work, CancellationToken token)
    {
        var resolved = await ResolveBindingAsync(work.SoftwareId, work.BindingId, true, token);
        if (resolved is null || !options.IsAvailable(resolved)) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        return await repository.BindingByIdAsync(work.BindingId, true, token) ?? throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    }
    private GitLabBinding ToBinding(ReleaseSourceBinding stored) => new(stored.Id, stored.SoftwareId, stored.SubjectId, stored.BaseUri,
        stored.ProjectId, stored.PackageName, stored.Managed ? stored.Enabled : options.Bindings.Any(b => b.Id == stored.Id && b.Enabled),
        stored.ConnectionId, stored.SourceRevision);
    private ReleaseSourceView View(ReleaseSourceBinding stored)
    {
        var binding = ToBinding(stored); var available = options.IsAvailable(binding);
        return new(stored.SoftwareId, "GitLab", binding.Enabled && available, stored.BaseUri, stored.ProjectId, stored.PackageName,
            stored.LastSyncedAt, stored.LastErrorCode, stored.LatestWorkId, stored.Id, stored.ConnectionId, stored.Revision, available, stored.Managed);
    }
    private static void ValidateFact(GitLabBinding binding, GitLabReleaseFacts facts)
    {
        if (!facts.Manifest.IsValid() || facts.Manifest.ProjectId != binding.ProjectId || facts.Manifest.PackageName != binding.PackageName ||
            string.IsNullOrWhiteSpace(facts.ReleaseName) || facts.ReleaseName.Length > 2000 || !Uri.TryCreate(facts.PipelineUrl, UriKind.Absolute, out var url) ||
            !new Uri(binding.BaseUri).IsBaseOf(url)) throw new RequestRejectedException(RequestFailure.ValidationFailed);
    }
    private Task<ReleaseSyncWork?> Find(Guid id, bool protect, CancellationToken token) => repository.WorkAsync(id, protect, token);
    private async Task<ReleaseSyncWork> Existing(Guid id, bool protect, CancellationToken token) => await Find(id, protect, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
    private void RequireWrite() { if (unit.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
    private static ReleaseSyncWorkView View(ReleaseSyncWork w) => new(w.Id, w.SoftwareId, w.Kind, w.Tag, w.Stage, w.LastErrorCode, w.ReleaseId, w.CreatedAt, w.CompletedAt, w.Revision);
    private static GitLabWork Work(ReleaseSyncWork w) => new(w.Id, w.BindingId, w.SoftwareId, w.Kind, w.Tag, w.Stage, w.Page,
        GitLabFacts.Decode(w.CandidateJson), w.InitialScan, w.ReleaseId, w.ReceiveToken, w.LeaseToken, w.LeaseGeneration, w.LeaseUntil);
}
