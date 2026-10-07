using Microsoft.EntityFrameworkCore;
using Svm.Core.Instances;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Catalog;

internal sealed class SiteAssetRepository(SvmDbContext context, IUnitOfWork unitOfWork) : ISiteAssetRepository
{
    public async Task EnsureSiteAsync(Guid siteId, bool write, CancellationToken token)
    {
        if (write)
        {
            RequireWrite();
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ins.site_identity (\"Singleton\",\"SiteId\") VALUES (1,{siteId}) ON CONFLICT (\"Singleton\") DO NOTHING", token);
        }
        var rows = await context.Set<SiteIdentity>().FromSqlRaw("SELECT * FROM ins.site_identity" + (write ? " FOR SHARE" : "")).AsNoTracking().ToListAsync(token);
        if (rows.Count > 1 || rows.Any(x => x.SiteId != siteId)) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    }
    public async Task<ProductionProcess?> ProcessAsync(Guid id, bool protect, CancellationToken token)
    {
        if (protect) RequireWrite();
        var query = context.Set<ProductionProcess>().FromSqlRaw("SELECT * FROM ins.processes WHERE \"Id\"={0}" + (protect ? " FOR UPDATE" : ""), id);
        return await (protect ? query.AsTracking() : query.AsNoTracking()).SingleOrDefaultAsync(token);
    }
    public async Task<ProductionDevice?> DeviceAsync(Guid id, bool protect, CancellationToken token)
    {
        if (protect) RequireWrite();
        var query = context.Set<ProductionDevice>().FromSqlRaw("SELECT * FROM ins.devices WHERE \"Id\"={0}" + (protect ? " FOR UPDATE" : ""), id);
        return await (protect ? query.AsTracking() : query.AsNoTracking()).SingleOrDefaultAsync(token);
    }
    public async Task<DeviceSoftwareBinding?> BindingAsync(Guid deviceId, Guid softwareId, bool protect, CancellationToken token)
    {
        if (protect) RequireWrite();
        var query = context.Set<DeviceSoftwareBinding>().FromSqlRaw("SELECT * FROM ins.device_software_bindings WHERE \"DeviceId\"={0} AND \"SoftwareId\"={1}" + (protect ? " FOR UPDATE" : ""), deviceId, softwareId);
        return await (protect ? query.AsTracking() : query.AsNoTracking()).SingleOrDefaultAsync(token);
    }
    public Task<DeviceSoftwareBinding?> BindingByIdAsync(Guid id, CancellationToken token) =>
        context.Set<DeviceSoftwareBinding>().FromSqlInterpolated($"SELECT * FROM ins.device_software_bindings WHERE \"Id\"={id}").AsNoTracking().SingleOrDefaultAsync(token);
    public async Task<bool> ProcessCodeExistsAsync(Guid siteId, string code, CancellationToken token)
    {
        RequireWrite(); await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({siteId.ToString()} || {code}, 71002))", token);
        return await context.Set<ProductionProcess>().AnyAsync(x => x.SiteId == siteId && x.Code == code, token);
    }
    public async Task<bool> DeviceNoExistsAsync(string deviceNo, CancellationToken token)
    {
        RequireWrite(); await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({deviceNo}, 71003))", token);
        return await context.Set<ProductionDevice>().AnyAsync(x => x.DeviceNo == deviceNo, token);
    }
    public void AddProcess(ProductionProcess process) { RequireWrite(); context.Add(process); }
    public void AddDevice(ProductionDevice device) { RequireWrite(); context.Add(device); }
    public void AddBinding(DeviceSoftwareBinding binding) { RequireWrite(); context.Add(binding); }
    private void RequireWrite()
    { if (context.Database.CurrentTransaction is null || unitOfWork.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
