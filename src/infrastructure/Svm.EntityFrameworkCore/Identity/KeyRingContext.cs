using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Svm.EntityFrameworkCore.Framework;

namespace Svm.EntityFrameworkCore.Identity;

// Technical key storage has its own short transactions. It cannot save module entities.
internal sealed class KeyRingContext(WriteDataSource source) : DbContext(
    new DbContextOptionsBuilder<KeyRingContext>().UseNpgsql(source.DataSource.CreateConnection(), contextOwnsConnection: true).Options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<DataProtectionKey>().ToTable("data_protection_keys", "framework");
}
public static class KeyRingRegistration
{
    public static IDataProtectionBuilder PersistSvmKeysToDatabase(this IDataProtectionBuilder builder)
    {
        builder.Services.AddScoped<KeyRingContext>();
        return builder.PersistKeysToDbContext<KeyRingContext>();
    }
}
