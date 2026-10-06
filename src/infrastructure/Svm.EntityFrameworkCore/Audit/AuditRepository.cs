using Svm.Core.Audit;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Audit;

internal sealed class AuditRepository(SvmDbContext context) : IAuditRepository
{
    public void Append(AuditEntry entry)
    {
        if (context.Database.CurrentTransaction is null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting);
        context.Add(entry);
    }
}
