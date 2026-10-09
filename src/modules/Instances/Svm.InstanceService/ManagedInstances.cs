using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Svm.Core.Instances;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Catalog;
using Svm.Services.Contracts.Instances;

namespace Svm.InstanceService;

public static class ManagedInstanceRegistration
{
    public static IServiceCollection AddSvmManagedInstances(this IServiceCollection services) => services.AddScoped<IManagedInstances,ManagedInstances>();
}
internal sealed class ManagedInstances(IManagedInstanceRepository repository, IUnitOfWork unitOfWork, SiteCatalogOptions options, TimeProvider clock) : IManagedInstances
{
    public async Task VerifyInstallationEvidenceAsync(Guid evidenceId, Guid softwareId, Guid releaseId, string version, CancellationToken token)
    {
        var evidence = await repository.EvidenceAsync(evidenceId, token) ?? throw Missing();
        if (evidence.SoftwareId != softwareId || evidence.InstalledReleaseId != releaseId) throw Missing();
        if (evidence.InstallationState != "Installed" || evidence.InstalledVersion != version)
            throw new RequestRejectedException(RequestFailure.InvalidState);
    }
    public async Task<InstanceIdentity?> GetIdentityAsync(Guid id,bool protect,CancellationToken token)
    { var i=await repository.GetAsync(id,protect,token); return i is null ? null : View(i); }
    public Task CreateAsync(Guid id,Guid softwareId,Guid deviceId,Guid installationKey,CancellationToken token)
    { token.ThrowIfCancellationRequested(); RequireWrite(); repository.Add(new(new(id),softwareId,deviceId,installationKey),new(new(id),softwareId)); return Task.CompletedTask; }
    public async Task<long> InvalidateStreamAsync(Guid instanceId,CancellationToken token)
    { RequireWrite(); return (await repository.SnapshotAsync(instanceId,true,token)).Advance(false); }
    public async Task<InstanceIdentity> LifecycleAsync(Guid id,long revision,string lifecycle,CancellationToken token)
    {
        RequireWrite(); var i=await repository.GetAsync(id,true,token) ?? throw Missing();
        if(i.Revision!=revision) throw new RequestRejectedException(RequestFailure.RevisionConflict); i.SetLifecycle(lifecycle); return View(i);
    }
    public async Task<ClientContext> ContextAsync(Guid id,CancellationToken token)
    {
        var i=await repository.GetAsync(id,false,token) ?? throw Missing(); var s=await repository.SnapshotAsync(id,false,token); var site=options.Require();
        return new(id,i.SoftwareId,site.SiteId,site.SiteTimeZone,s.StreamEpoch,s.ReportSeq,s.LastAcceptedAt);
    }
    public async Task<StreamResult> OpenStreamAsync(Guid id,long expectedEpoch,CancellationToken token)
    {
        RequireWrite(); var s=await repository.SnapshotAsync(id,true,token);
        if(s.StreamEpoch!=expectedEpoch) throw new RequestRejectedException(RequestFailure.RevisionConflict);
        var epoch=s.Advance(true); repository.AddStreamReceipt(new() { OperationId=unitOfWork.CurrentOperationId!.Value,InstanceId=new(id),Epoch=epoch }); return new(epoch);
    }
    public async Task<StreamResult> StreamResultAsync(Guid operationId,CancellationToken token) =>
        new((await repository.StreamReceiptAsync(operationId,token) ?? throw Missing()).Epoch);
    public async Task<ReportResult?> FindReportAsync(Guid id,StateReport report,CancellationToken token)
    {
        var s=await repository.SnapshotAsync(id,unitOfWork.CurrentOperationId is not null,token);
        return Existing(s,report);
    }
    public async Task<ReportResult> ReportAsync(Guid id,StateReport report,CancellationToken token)
    {
        RequireWrite(); var s=await repository.SnapshotAsync(id,true,token);
        if(Existing(s,report) is { } existing) return existing;
        if(report.StreamEpoch!=s.StreamEpoch || !s.StreamOpen) return Result(s,false);
        var now=clock.GetUtcNow(); var digest=Digest(report); var installationDigest=Hash(JsonSerializer.Serialize(new { report.InstallationState,report.InstalledReleaseId,report.InstalledVersion,InstalledAt=report.InstalledAt?.ToUniversalTime() }));
        Guid? evidenceId=null;
        if(s.LastAcceptedAt is null || s.InstallationDigest!=installationDigest)
        {
            evidenceId=Guid.NewGuid(); repository.AddEvidence(new() { Id=evidenceId.Value,InstanceId=new(id),SoftwareId=s.SoftwareId,StreamEpoch=report.StreamEpoch,ReportSeq=report.ReportSeq,
                InstallationState=report.InstallationState,InstalledReleaseId=report.InstalledReleaseId,InstalledVersion=report.InstalledVersion,InstalledAt=report.InstalledAt,
                ReceivedAt=now,ReportedRunningState=report.RunningState,RequestDigest=digest });
        }
        s.Accept(report.ReportSeq,now,JsonSerializer.Serialize(Normalize(report)),digest,installationDigest,evidenceId); return Result(s,true);
    }
    private static ReportResult? Existing(InstanceSnapshot s,StateReport report)
    {
        if(report.StreamEpoch<s.StreamEpoch || report.StreamEpoch==s.StreamEpoch && report.ReportSeq<s.ReportSeq || !s.StreamOpen && report.StreamEpoch<=s.StreamEpoch) return Result(s,false);
        if(report.StreamEpoch!=s.StreamEpoch || report.ReportSeq!=s.ReportSeq || s.RequestDigest is null) return null;
        if(s.RequestDigest!=Digest(report)) throw new RequestRejectedException(RequestFailure.ReportConflict);
        return Result(s,true);
    }
    private static StateReport Normalize(StateReport report) => report with { ReportedAt=report.ReportedAt.ToUniversalTime(),InstalledAt=report.InstalledAt?.ToUniversalTime() };
    private static string Digest(StateReport report) => Hash(JsonSerializer.Serialize(Normalize(report)));
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static ReportResult Result(InstanceSnapshot s,bool applied) => new(applied,s.StreamEpoch,s.ReportSeq,s.LastAcceptedAt,applied?s.EvidenceId:null);
    private static InstanceIdentity View(ManagedInstance i) => new(i.Id.Value,i.SoftwareId,i.DeviceId,i.Lifecycle,i.Revision);
    private static RequestRejectedException Missing() => new(RequestFailure.ResourceNotFound);
    private void RequireWrite() { if(unitOfWork.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
