using Svm.Application.Packages;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Identity;
using Svm.Services.Contracts.Packages;
using Svm.Services.Contracts.Plc;
namespace Svm.Application.Plc;

internal sealed class CreatePlcCredentialAdapter(IPlcUploadAccess access,IPersonnelCrypto crypto) : PackageAdapter<CreatePlcCredentialCommand,PlcCredentialView>
{
    public override OperationRequestData Describe(CreatePlcCredentialCommand x)=>new(x.Key,OperationValue.Object(G("softwareId",x.SoftwareId)),OperationValue.Object(
        S("name",x.Name),S("secretHash",crypto.HashSecret(x.SecretMaterial)),new("expiresAt",OperationValue.Timestamp(x.ExpiresAt)),
        new("scope",OperationValue.Array(x.Scope.OrderBy(p=>p.BindingId).Select(p=>OperationValue.Object(G("bindingId",p.BindingId),N("bindingRevision",p.BindingRevision))).ToArray()))));
    public override async Task<OperationResult<PlcCredentialView>> RestoreAsync(OperationResultReference r,CancellationToken token)=>OperationResult<PlcCredentialView>.Completed(r.OperationId,await access.GetAsync(Id(r),false,token),Id(r));
}
internal sealed class RevokePlcCredentialAdapter(IPlcUploadAccess access) : PackageAdapter<RevokePlcCredentialCommand,PlcCredentialView>
{
    public override OperationRequestData Describe(RevokePlcCredentialCommand x)=>new(x.Key,OperationValue.Object(G("credentialId",x.CredentialId)),OperationValue.Object(N("expectedRevision",x.ExpectedRevision),S("reason",x.Reason)));
    public override async Task<OperationResult<PlcCredentialView>> RestoreAsync(OperationResultReference r,CancellationToken token)=>OperationResult<PlcCredentialView>.Completed(r.OperationId,await access.GetAsync(Id(r),false,token),Id(r));
}
internal sealed class PublishPlcReleaseAdapter(IReleases releases,IPackages packages) : PackageAdapter<PublishPlcReleaseCommand,ReleaseView>
{
    public override OperationRequestData Describe(PublishPlcReleaseCommand x)=>new(x.Key,OperationValue.Object(G("releaseId",x.ReleaseId)),OperationValue.Object(
        N("expectedRevision",x.ExpectedRevision),G("bindingId",x.BindingId),N("bindingRevision",x.BindingRevision),new("fieldAt",OperationValue.Timestamp(x.FieldAt)),
        S("verification",x.Verification),S("materials",x.Materials),S("publishReason",x.PublishReason),S("publishConclusion",x.PublishConclusion)));
    public override async Task<OperationResult<ReleaseView>> RestoreAsync(OperationResultReference r,CancellationToken token)
    { var release=await releases.GetAsync(Id(r),false,token); return OperationResult<ReleaseView>.Completed(r.OperationId,release with {DownloadAvailable=(await packages.GetAsync(release.PackageId,false,false,token)).DownloadAvailable},release.Id); }
}
internal sealed class RecordPlcUsageAdapter(IPlcUsage usage) : PackageAdapter<RecordPlcUsageCommand,PlcUsageView>
{
    public override OperationRequestData Describe(RecordPlcUsageCommand x)=>new(x.Key,OperationValue.Object(G("bindingId",x.BindingId)),OperationValue.Object(
        N("bindingRevision",x.BindingRevision),G("releaseId",x.ReleaseId),N("expectedRevision",x.ExpectedRevision),new("fieldAt",OperationValue.Timestamp(x.FieldAt)),
        S("note",x.Note),S("materials",x.Materials),new("correctsId",x.CorrectsId is {} id?OperationValue.Identifier(id):OperationValue.Null)));
    public override async Task<OperationResult<PlcUsageView>> RestoreAsync(OperationResultReference r,CancellationToken token)=>OperationResult<PlcUsageView>.Completed(r.OperationId,await usage.GetAsync(Id(r),token),Id(r));
}
internal sealed class CreatePlcArchiveAdapter(IReleases releases,IPackages packages) : PackageAdapter<CreatePlcArchiveCommand,ReleaseUploadResult>
{
    public override OperationRequestData Describe(CreatePlcArchiveCommand x)=>new(x.Key,OperationValue.Object(G("bindingId",x.BindingId)),OperationValue.Object(N("bindingRevision",x.BindingRevision),S("changeLevel",x.ChangeLevel),S("changeSummary",x.ChangeSummary),S("changeReason",x.ChangeReason),S("expectedVersion",x.ExpectedVersion),S("fileName",x.Package.FileName),N("sizeBytes",x.Package.SizeBytes),S("sha256",x.Package.Sha256.ToLowerInvariant())));
    public override async Task<OperationResult<ReleaseUploadResult>> RestoreAsync(OperationResultReference r,CancellationToken token)
    { var release=await releases.GetAsync(Id(r),false,token); var upload=await packages.UploadIdAsync(release.PackageId,token);return OperationResult<ReleaseUploadResult>.Completed(r.OperationId,new(release,upload,$"/api/v1/plc-upload/uploads/{upload:D}/content"),release.Id); }
}
