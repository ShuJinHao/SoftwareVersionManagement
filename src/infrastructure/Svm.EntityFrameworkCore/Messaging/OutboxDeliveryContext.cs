using Microsoft.EntityFrameworkCore;
using Npgsql;
using Svm.EntityFrameworkCore.Framework;

namespace Svm.EntityFrameworkCore.Messaging;

// A separate scoped tracker for native delivery progress. Module types are absent from this model.
internal sealed class OutboxDeliveryContext(WriteDataSource source) : DbContext(
    new DbContextOptionsBuilder<OutboxDeliveryContext>()
        .UseNpgsql(source.DataSource.CreateConnection(), contextOwnsConnection: true).Options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => OutboxModel.Configure(modelBuilder);
    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new InvalidOperationException("Outbox delivery uses asynchronous saves only.");
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await Database.OpenConnectionAsync(cancellationToken);
        await PostgresUnitOfWork.EnsureRuntimeRoleAsync((NpgsqlConnection)Database.GetDbConnection(), cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
