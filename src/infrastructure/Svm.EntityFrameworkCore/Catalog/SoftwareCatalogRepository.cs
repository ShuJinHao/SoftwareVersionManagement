using Microsoft.EntityFrameworkCore;
using Svm.Core.Releases;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Catalog;

internal sealed class SoftwareCatalogRepository(SvmDbContext context, IUnitOfWork unitOfWork) : ISoftwareCatalogRepository
{
    public async Task<SoftwareProduct?> GetAsync(Guid id, bool protect, CancellationToken token)
    {
        if (protect) RequireWrite();
        var query = context.Set<SoftwareProduct>().FromSqlRaw("SELECT * FROM rel.software WHERE \"Id\"={0}" + (protect ? " FOR UPDATE" : ""), id);
        return await (protect ? query.AsTracking() : query.AsNoTracking()).SingleOrDefaultAsync(token);
    }
    public async Task<bool> CodeExistsAsync(string code, CancellationToken token)
    {
        RequireWrite();
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({code}, 71001))", token);
        return await context.Set<SoftwareProduct>().AnyAsync(x => x.Code == code, token);
    }
    public void Add(SoftwareProduct software) { RequireWrite(); context.Add(software); }
    private void RequireWrite()
    { if (context.Database.CurrentTransaction is null || unitOfWork.CurrentOperationId is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting); }
}
