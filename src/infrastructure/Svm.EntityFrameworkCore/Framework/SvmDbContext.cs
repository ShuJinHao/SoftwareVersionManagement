using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MassTransit.EntityFrameworkCoreIntegration;
using Svm.EntityFrameworkCore.Messaging;
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
    internal bool OutboxSealed { get; set; }
    internal ConsumerTransactionState ConsumerTransactions { get; } = new();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(ConsumerTransactions, new ConsumerTechnicalSaveGuard(this));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("framework");
        Identity.PersonnelModel.Configure(modelBuilder);
        Operations.OperationResultModel.Configure(modelBuilder);
        Messaging.OutboxModel.Configure(modelBuilder);
        Catalog.CatalogModel.Configure(modelBuilder);
        Instances.InstanceModel.Configure(modelBuilder);
        Packages.ReleasePackageModel.Configure(modelBuilder);
        Tasks.TaskModel.Configure(modelBuilder);
        Tasks.IntegrationMaterialModel.Configure(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) => throw new InvalidOperationException("Only the unit of work can save module changes.");
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureNativeTechnicalSave();
        var count = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        EnsureNativeTechnicalSave();
        return count;
    }

    internal void EnsureNativeTechnicalSave()
    {
        if (!ConsumerTransactions.Owns(Database.CurrentTransaction) || ConsumerTransactions.BusinessActive || PendingOperationResult is not null ||
            ChangeTracker.Entries().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted &&
                e.Entity is not (InboxState or OutboxState or OutboxMessage)))
            throw new InvalidOperationException("Only the unit of work can save module changes.");
        ConsumerTransactions.Validate();
    }

    internal Task<int> SaveWithinUnitOfWorkAsync(CancellationToken cancellationToken)
    {
        if (PendingOperationResult is { } operationId)
            throw new PersistenceException(PersistenceFailure.OperationAborted, operationId);
        return base.SaveChangesAsync(true, cancellationToken);
    }
}
