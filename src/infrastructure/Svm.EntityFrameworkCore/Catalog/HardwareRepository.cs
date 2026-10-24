using Microsoft.EntityFrameworkCore;
using Svm.Core.Instances;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Catalog;

internal sealed class HardwareRepository(SvmDbContext context, IUnitOfWork unit) : IHardwareRepository
{
    public async Task<HardwareAsset?> HardwareAsync(Guid id, bool protect, CancellationToken token)
    {
        if (protect) RequireWrite();
        var query = context.Set<HardwareAsset>().FromSqlRaw("SELECT * FROM ins.hardware WHERE \"Id\"={0}" + (protect ? " FOR UPDATE" : ""), id);
        return await (protect ? query.AsTracking() : query.AsNoTracking()).SingleOrDefaultAsync(token);
    }
    public async Task<HardwareSoftwareBinding?> BindingAsync(Guid id, bool protect, CancellationToken token)
    {
        if (protect) RequireWrite();
        var query = context.Set<HardwareSoftwareBinding>().FromSqlRaw("SELECT * FROM ins.hardware_software_bindings WHERE \"Id\"={0}" + (protect ? " FOR UPDATE" : ""), id);
        return await (protect ? query.AsTracking() : query.AsNoTracking()).SingleOrDefaultAsync(token);
    }
    public async Task<HardwareSoftwareBinding?> BindingAsync(Guid hardwareId, Guid softwareId, bool protect, CancellationToken token)
    {
        if (protect) RequireWrite();
        var query = context.Set<HardwareSoftwareBinding>().FromSqlRaw("SELECT * FROM ins.hardware_software_bindings WHERE \"HardwareId\"={0} AND \"SoftwareId\"={1}" + (protect ? " FOR UPDATE" : ""), hardwareId, softwareId);
        return await (protect ? query.AsTracking() : query.AsNoTracking()).SingleOrDefaultAsync(token);
    }
    public void Add(HardwareAsset hardware) { RequireWrite(); context.Add(hardware); }
    public void Add(HardwareSoftwareBinding binding) { RequireWrite(); context.Add(binding); }
    private void RequireWrite()
    { if (unit.CurrentOperationId is null || context.Database.CurrentTransaction is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
