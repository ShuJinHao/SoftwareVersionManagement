using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Svm.EntityFrameworkCore.Framework;
using Xunit;
namespace Svm.FrameworkTests;
[Trait("Category", "Business")]
public sealed class PackageMigrationTests
{
    [Fact] public async Task SevenMigrationUpgradeKeepsExistingInstanceDataAndModelAndLeastPrivileges()
    {
        var db = new PersistenceDatabase();
        try
        {
            await db.CreateAsync(false); await using (var c = Context(db)) await c.GetService<IMigrator>().MigrateAsync("20261009000100_InstanceAccess");
            await PersistenceDatabase.ExecuteAsync(db.MigrationConnection, """
                INSERT INTO ins.site_identity VALUES (1,'11111111-1111-4111-8111-111111111111');
                INSERT INTO rel.software VALUES ('22222222-2222-4222-8222-222222222222','MIGRATION-S','迁移夹具','Vision',NULL,1);
                INSERT INTO ins.processes VALUES ('33333333-3333-4333-8333-333333333333','11111111-1111-4111-8111-111111111111','MIGRATION-P','迁移工序',1);
                INSERT INTO ins.devices VALUES ('44444444-4444-4444-8444-444444444444','33333333-3333-4333-8333-333333333333','MIGRATION-D','迁移设备',1);
                INSERT INTO ins.device_software_bindings VALUES ('55555555-5555-4555-8555-555555555555','44444444-4444-4444-8444-444444444444','22222222-2222-4222-8222-222222222222',1,true,true);
                INSERT INTO ins.instances VALUES ('66666666-6666-4666-8666-666666666666','22222222-2222-4222-8222-222222222222','44444444-4444-4444-8444-444444444444','77777777-7777-4777-8777-777777777777','Active',1);
                """);
            var before = await Digest(db); var script = db.Runner.GenerateScript(); Assert.Contains("20261010000100_ReleasesAndPackages", script);
            var directory = Path.Combine(OutboxFixture.Root, "artifacts", "releases-packages"); Directory.CreateDirectory(directory); await File.WriteAllTextAsync(Path.Combine(directory, "releases-packages-upgrade.sql"), script);
            var result = await db.Runner.ApplyAsync(default); Assert.Equal(10, result.Applied.Count); Assert.Empty(result.Pending); Assert.Equal(before, await Digest(db)); await using (var c = Context(db)) Assert.False(c.Database.HasPendingModelChanges());
            foreach (var table in new[] { "rel.releases", "pkg.packages", "pkg.works", "pkg.replicas", "pkg.download_sessions", "pkg.dispatches", "pkg.receive_attempts" }) Assert.Equal(0, await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection, "SELECT count(*) FROM " + table));
            await Assert.ThrowsAsync<PostgresException>(() => PersistenceDatabase.ExecuteAsync(db.WriterConnection, "DELETE FROM pkg.dispatches")); await Assert.ThrowsAsync<PostgresException>(() => PersistenceDatabase.ExecuteAsync(db.ReaderConnection, "INSERT INTO pkg.dispatches VALUES(gen_random_uuid(),gen_random_uuid(),1)")); Assert.Equal(result.Applied, (await db.Runner.ApplyAsync(default)).Applied);
        }
        finally { await db.DisposeAsync(); }
    }
    private static SvmDbContext Context(PersistenceDatabase db) => new(new DbContextOptionsBuilder<SvmDbContext>().UseNpgsql(db.MigrationConnection, p => p.MigrationsHistoryTable("__EFMigrationsHistory", "framework")).Options);
    private static Task<string> Digest(PersistenceDatabase db) => PersistenceDatabase.ScalarAsync<string>(db.MigrationConnection, "SELECT md5(string_agg(value,'|' ORDER BY value)) FROM (SELECT row_to_json(x)::text value FROM ins.instances x UNION ALL SELECT row_to_json(x)::text FROM ins.device_software_bindings x UNION ALL SELECT row_to_json(x)::text FROM rel.software x) q");
}
