using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Framework;

internal sealed class SvmDbContext : DbContext
{
    public SvmDbContext(WriteDataSource source, IEnumerable<IInterceptor> interceptors)
        : this(new DbContextOptionsBuilder<SvmDbContext>()
            .UseNpgsql(source.DataSource.CreateConnection(), contextOwnsConnection: true,
                postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "framework"))
            .AddInterceptors(interceptors).Options) { }

    internal SvmDbContext(DbContextOptions<SvmDbContext> options) : base(options) { }

    internal Guid? PendingOperationResult { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("framework");
        Identity.PersonnelModel.Configure(modelBuilder);
        Operations.OperationResultModel.Configure(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) => throw new InvalidOperationException("Only the unit of work can save module changes.");
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Only the unit of work can save module changes.");

    internal Task<int> SaveWithinUnitOfWorkAsync(CancellationToken cancellationToken)
    {
        if (PendingOperationResult is { } operationId)
            throw new PersistenceException(PersistenceFailure.OperationAborted, operationId);
        return base.SaveChangesAsync(true, cancellationToken);
    }
}
