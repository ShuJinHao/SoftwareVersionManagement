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
        var binding = options.Bindings.SingleOrDefault(b => b.SoftwareId == softwareId && b.SubjectId == subject && b.Enabled);
        if (binding is null) return false;
        var stored = await repository.BindingAsync(softwareId, false, token);
        var release = await releases.GetAsync(releaseId, false, token);
        return stored is not null && Matches(stored, binding) && release?.SourceBindingId == binding.Id && release.SourceKind == "GitLab" && release.CreatedBy == subject;
    }
    public async Task<ReleaseSourceView> SourceAsync(Guid id, CancellationToken token)
    {
        var configured = options.Bindings.SingleOrDefault(b => b.SoftwareId == id);
        var stored = await repository.BindingAsync(id, false, token);
        return new(id, configured is not null || stored is not null ? "GitLab" : "Manual",
            configured?.Enabled == true && (stored is null || Matches(stored, configured)), configured?.BaseUri ?? stored?.BaseUri,
            configured?.ProjectId ?? stored?.ProjectId, configured?.PackageName ?? stored?.PackageName,
            stored?.LastSyncedAt, stored?.LastErrorCode, stored?.LatestWorkId);
    }
    public async Task<ReleaseSyncWorkView> ViewAsync(Guid id, CancellationToken token) => View(await Existing(id, false, token));
    public async Task<GitLabWork> WorkAsync(Guid id, bool protect, CancellationToken token) => Work(await Existing(id, protect, token));
    public async Task<ReleaseSyncWorkView> ScheduleAsync(Guid id, string? tag, bool automatic, CancellationToken token)
    {
        var binding = await Binding(id, token); var now = clock.GetUtcNow();
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
        var known = await Existing(id, false, token); await Binding(known.SoftwareId, token);
        var work = await Existing(id, true, token); return work.Claim(leaseToken, clock.GetUtcNow(), options.LeaseSeconds) ? Work(work) : null;
    }
    public async Task<GitLabWork> ProtectAsync(GitLabLease lease, CancellationToken token)
    {
        var known = await Existing(lease.WorkId, false, token); await Binding(known.SoftwareId, token);
        var work = await Existing(lease.WorkId, true, token);
        if (!work.Owns(lease.Token, lease.Generation, clock.GetUtcNow())) throw new RequestRejectedException(RequestFailure.InvalidState);
        work.Renew(clock.GetUtcNow().AddSeconds(options.LeaseSeconds)); return Work(work);
    }
    public async Task ScanAsync(GitLabLease lease, IReadOnlyList<GitLabReleaseFacts> facts, IReadOnlyList<string> tags, IReadOnlyList<string> pendingTags, int? next, CancellationToken token)
    {
        await ProtectAsync(lease, token); var work = await Existing(lease.WorkId, true, token);
        if (work.Kind != "Scan" || next is { } page && (page != work.Page + 1 || page > options.MaxPages)) throw new RequestRejectedException(RequestFailure.InvalidState);
        var binding = await Binding(work.SoftwareId, token); var candidate = GitLabFacts.Decode(work.CandidateJson);
        foreach (var fact in facts)
        {
            ValidateFact(options.Require(work.SoftwareId), fact);
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
        var config = options.Require(work.SoftwareId); ValidateFact(config, facts);
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
        (await Binding(work.SoftwareId, token)).Error(code);
        work.Delay(code, clock.GetUtcNow().AddSeconds(options.PollSeconds), code == "VERSION_CONFLICT");
    }
    public async Task CompleteReleaseAsync(Guid id, Guid packageId, CancellationToken token)
    {
        RequireWrite(); var release = await releases.GetAsync(id, true, token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        if (release.SourceKind != "GitLab" || release.PackageId != packageId || release.State == "Disabled") throw new RequestRejectedException(RequestFailure.InvalidState);
        if (release.State == "Staging") release.OpenImportedFormal(clock.GetUtcNow());
        var work = await repository.FindAsync(release.SourceBindingId!.Value, release.SourceTag, false, token);
        work?.Complete(clock.GetUtcNow());
        (await Binding(release.SoftwareId, token)).Scanned(clock.GetUtcNow(), false);
    }
    private async Task<ReleaseSourceBinding> Binding(Guid id, CancellationToken token)
    {
        RequireWrite(); var config = options.Require(id);
        if (await software.GetAsync(id, true, token) is null) throw new RequestRejectedException(RequestFailure.ResourceNotFound);
        var binding = await repository.BindingAsync(id, true, token);
        if (binding is null) { binding = new(config.Id, id, config.SubjectId, config.BaseUri, config.ProjectId, config.PackageName); repository.Add(binding); }
        else if (!Matches(binding, config)) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
        return binding;
    }
    private static bool Matches(ReleaseSourceBinding stored, GitLabBinding config) => stored.Id == config.Id && stored.SubjectId == config.SubjectId &&
        stored.BaseUri == config.BaseUri && stored.ProjectId == config.ProjectId && stored.PackageName == config.PackageName;
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
