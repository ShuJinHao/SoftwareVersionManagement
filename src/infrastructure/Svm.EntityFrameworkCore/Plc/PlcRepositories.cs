using Microsoft.EntityFrameworkCore;
using Svm.Core.Identity;
using Svm.Core.Instances;
using Svm.Core.Releases;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;
namespace Svm.EntityFrameworkCore.Plc;

internal sealed class PlcCredentialRepository(SvmDbContext db, IUnitOfWork unit) : IPlcCredentialRepository
{
    public async Task<PlcUploadCredential?> GetAsync(Guid id, bool protect, CancellationToken token)
    {
        if (protect) Write();
        var q = db.Set<PlcUploadCredential>().FromSqlRaw("SELECT * FROM iam.plc_upload_credentials WHERE \"Id\"={0}" + (protect ? " FOR UPDATE" : ""), id);
        return await (protect ? q.AsTracking() : q.AsNoTracking()).SingleOrDefaultAsync(token);
    }
    public void Add(PlcUploadCredential x) { Write(); db.Add(x); }
    private void Write() { if (unit.CurrentOperationId is null || db.Database.CurrentTransaction is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
internal sealed class PlcPublicationRepository(SvmDbContext db, IUnitOfWork unit) : IPlcPublicationRepository
{
    public Task<PlcPublication?> GetAsync(Guid releaseId, CancellationToken token) => db.Set<PlcPublication>().AsNoTracking().SingleOrDefaultAsync(x => x.ReleaseId == releaseId, token);
    public void Add(PlcPublication x) { if (unit.CurrentOperationId is null || db.Database.CurrentTransaction is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); db.Add(x); }
}
internal sealed class PlcUsageRepository(SvmDbContext db, IUnitOfWork unit) : IPlcUsageRepository
{
    public async Task<PlcUsageState?> StateAsync(Guid id, bool protect, CancellationToken token)
    {
        if (protect) Write();
        // The caller already holds the hardware/association lock, including when this row does not yet exist.
        var q = db.Set<PlcUsageState>().FromSqlRaw("SELECT * FROM ins.plc_usage_state WHERE \"Id\"={0}" + (protect ? " FOR UPDATE" : ""), id);
        return await (protect ? q.AsTracking() : q.AsNoTracking()).SingleOrDefaultAsync(token);
    }
    public Task<PlcUsageRecord?> GetAsync(Guid id, CancellationToken token) => db.Set<PlcUsageRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
    public Task<bool> IsCorrectedAsync(Guid id, CancellationToken token) => db.Set<PlcUsageRecord>().AnyAsync(x => x.CorrectsId == id, token);
    public Task<PlcUsageRecord?> LatestAsync(Guid bindingId, long generation, Guid? exclude, CancellationToken token) =>
        db.Set<PlcUsageRecord>().AsNoTracking().Where(x => x.BindingId == bindingId && x.BindingRevision == generation && x.Id != exclude &&
            !db.Set<PlcUsageRecord>().Any(y => y.CorrectsId == x.Id)).OrderByDescending(x => x.FieldAt).ThenByDescending(x => x.RecordedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(token);
    public void Add(PlcUsageState x) { Write(); db.Add(x); }
    public void Add(PlcUsageRecord x) { Write(); db.Add(x); }
    private void Write() { if (unit.CurrentOperationId is null || db.Database.CurrentTransaction is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
