using Svm.SharedKernel.Domain;
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
        var rows = await (write
            ? context.Set<SiteIdentity>().FromSqlRaw("SELECT * FROM ins.site_identity FOR SHARE")
            : context.Set<SiteIdentity>().FromSqlRaw("SELECT * FROM ins.site_identity")).AsNoTracking().ToListAsync(token);
        if (rows.Count > 1 || rows.Any(x => x.SiteId != siteId)) throw new RequestRejectedException(RequestFailure.ConfigurationInvalid);
    }
    public async Task<ProductionProcess?> ProcessAsync(Guid id, bool protect, CancellationToken token)
    {
        if (protect) RequireWrite();
        var query = protect
            ? context.Set<ProductionProcess>().FromSqlRaw("SELECT * FROM ins.processes WHERE \"Id\"={0} FOR UPDATE", id).AsTracking()
            : context.Set<ProductionProcess>().FromSqlRaw("SELECT * FROM ins.processes WHERE \"Id\"={0}", id).AsNoTracking();
        return await query.SingleOrDefaultAsync(token);
    }
    public async Task<ProductionDevice?> DeviceAsync(Guid id, bool protect, CancellationToken token)
    {
        if (protect) RequireWrite();
        var query = protect
            ? context.Set<ProductionDevice>().FromSqlRaw("SELECT * FROM ins.devices WHERE \"Id\"={0} FOR UPDATE", id).AsTracking()
            : context.Set<ProductionDevice>().FromSqlRaw("SELECT * FROM ins.devices WHERE \"Id\"={0}", id).AsNoTracking();
        return await query.SingleOrDefaultAsync(token);
    }
    public async Task<DeviceSoftwareBinding?> BindingAsync(Guid deviceId, Guid softwareId, bool protect, CancellationToken token)
    {
        if (protect) RequireWrite();
        var query = protect
            ? context.Set<DeviceSoftwareBinding>().FromSqlRaw("SELECT * FROM ins.device_software_bindings WHERE \"DeviceId\"={0} AND \"SoftwareId\"={1} FOR UPDATE", deviceId, softwareId).AsTracking()
            : context.Set<DeviceSoftwareBinding>().FromSqlRaw("SELECT * FROM ins.device_software_bindings WHERE \"DeviceId\"={0} AND \"SoftwareId\"={1}", deviceId, softwareId).AsNoTracking();
        return await query.SingleOrDefaultAsync(token);
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
    public async Task<string> NextProcessCodeAsync(CancellationToken token)
    { RequireWrite(); var value = await context.Database.SqlQueryRaw<long>("SELECT nextval('ins.process_code_seq') AS \"Value\"").SingleAsync(token); return "PR-" + value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture); }
    public async Task<string> NextDeviceNoAsync(CancellationToken token)
    { RequireWrite(); var value = await context.Database.SqlQueryRaw<long>("SELECT nextval('ins.device_no_seq') AS \"Value\"").SingleAsync(token); return "EQ-" + value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture); }
    public Task<bool> HasActiveHardwareBindingsAsync(Guid deviceBindingId, CancellationToken token) => context.Set<HardwareSoftwareBinding>().AnyAsync(x => x.DeviceBindingId == new StrongId<DeviceSoftwareBinding>(deviceBindingId) && x.IsActive, token);
    public void AddProcess(ProductionProcess process) { RequireWrite(); context.Add(process); }
    public void AddDevice(ProductionDevice device) { RequireWrite(); context.Add(device); }
    public void AddBinding(DeviceSoftwareBinding binding) { RequireWrite(); context.Add(binding); }
    private void RequireWrite()
    { if (context.Database.CurrentTransaction is null || unitOfWork.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
