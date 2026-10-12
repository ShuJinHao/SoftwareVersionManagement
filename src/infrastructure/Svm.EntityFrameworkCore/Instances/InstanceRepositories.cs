using Microsoft.EntityFrameworkCore;
using Svm.Core.Identity;
using Svm.Core.Instances;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;
using Svm.SharedKernel.Domain;

namespace Svm.EntityFrameworkCore.Instances;

internal sealed class InstanceAccessRepository(SvmDbContext context,IUnitOfWork unitOfWork) : IInstanceAccessRepository
{
    private IQueryable<T> Query<T>(string table,string field,Guid id,bool protect) where T:class
    {
        if(protect) RequireWrite();
        var q=context.Set<T>().FromSqlRaw($"SELECT * FROM iam.{table} WHERE \"{field}\"={{0}}"+(protect?" FOR UPDATE":""),id);
        return protect?q.AsTracking():q.AsNoTracking();
    }
    public Task<MachineSubject?> SubjectAsync(Guid id,bool protect,CancellationToken token) => Query<MachineSubject>("instance_subjects","Id",id,protect).SingleOrDefaultAsync(token);
    public Task<InstanceCredential?> CredentialAsync(Guid id,bool protect,CancellationToken token) => Query<InstanceCredential>("instance_credentials","Id",id,protect).SingleOrDefaultAsync(token);
    public Task<EnrollmentPermission?> EnrollmentAsync(Guid id,bool protect,CancellationToken token) => Query<EnrollmentPermission>("enrollment_grants","Id",id,protect).SingleOrDefaultAsync(token);
    public Task<RecoveryPermission?> RecoveryAsync(Guid id,bool protect,CancellationToken token) => Query<RecoveryPermission>("recovery_grants","Id",id,protect).SingleOrDefaultAsync(token);
    public async Task<InstanceRegistration?> RegistrationAsync(Guid softwareId,Guid installationKey,Guid grantId,Guid key,bool protect,CancellationToken token)
    {
        if(protect)
        {
            RequireWrite(); await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({softwareId.ToString()} || {installationKey.ToString()},71004))",token);
        }
        var rows=await context.Set<InstanceRegistration>().AsNoTracking().Where(x=>x.SoftwareId==softwareId && x.InstallationKey==installationKey || x.GrantId==new StrongId<EnrollmentPermission>(grantId) && x.Key==key).ToListAsync(token);
        if(rows.Count>1) throw new RequestRejectedException(RequestFailure.RegistrationConflict);
        return rows.SingleOrDefault();
    }
    public async Task RevokeCredentialsAsync(Guid subjectId,DateTimeOffset now,CancellationToken token)
    { RequireWrite(); await context.Set<InstanceCredential>().Where(x=>x.SubjectId==new StrongId<MachineSubject>(subjectId) && x.RevokedAt==null).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.RevokedAt,now).SetProperty(x=>x.Revision,x=>x.Revision+1),token); }
    public void AddSubject(MachineSubject v) { RequireWrite(); context.Add(v); }
    public void AddCredential(InstanceCredential v) { RequireWrite(); context.Add(v); }
    public void AddEnrollment(EnrollmentPermission v) { RequireWrite(); context.Add(v); }
    public void AddRecovery(RecoveryPermission v) { RequireWrite(); context.Add(v); }
    public void AddRegistration(InstanceRegistration v) { RequireWrite(); context.Add(v); }
    private void RequireWrite() { if(context.Database.CurrentTransaction is null || unitOfWork.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
internal sealed class ManagedInstanceRepository(SvmDbContext context,IUnitOfWork unitOfWork) : IManagedInstanceRepository
{
    public async Task<ManagedInstance?> GetAsync(Guid id,bool protect,CancellationToken token)
    {
        if(protect) RequireWrite(); var q=context.Set<ManagedInstance>().FromSqlRaw("SELECT * FROM ins.instances WHERE \"Id\"={0}"+(protect?" FOR UPDATE":""),id);
        return await (protect?q.AsTracking():q.AsNoTracking()).SingleOrDefaultAsync(token);
    }
    public async Task<InstanceSnapshot> SnapshotAsync(Guid id,bool protect,CancellationToken token)
    {
        if(protect) RequireWrite(); var q=context.Set<InstanceSnapshot>().FromSqlRaw("SELECT * FROM ins.instance_snapshots WHERE \"InstanceId\"={0}"+(protect?" FOR UPDATE":""),id);
        return await (protect?q.AsTracking():q.AsNoTracking()).SingleOrDefaultAsync(token) ?? throw new RequestRejectedException(RequestFailure.ResourceNotFound);
    }
    public Task<ReportStreamReceipt?> StreamReceiptAsync(Guid id,CancellationToken token) => context.Set<ReportStreamReceipt>().AsNoTracking().SingleOrDefaultAsync(x=>x.OperationId==id,token);
    public void Add(ManagedInstance i,InstanceSnapshot s) { RequireWrite(); context.Add(i); context.Add(s); }
    public void AddEvidence(InstallationEvidence e) { RequireWrite(); context.Add(e); }
    public void AddStreamReceipt(ReportStreamReceipt r) { RequireWrite(); context.Add(r); }
    private void RequireWrite() { if(context.Database.CurrentTransaction is null || unitOfWork.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
