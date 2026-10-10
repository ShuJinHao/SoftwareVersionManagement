using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class DeploymentMigrationTests
{
    [Fact]
    public async Task NineMigrationUpgradeRetainsExistingFactsAndFrozenTaskModel()
    {
        var db = new PersistenceDatabase();
        try
        {
            await db.CreateAsync(false);
            await using (var c = Context(db)) await c.GetService<IMigrator>().MigrateAsync("20261010000100_ReleasesAndPackages");
            await PersistenceDatabase.ExecuteAsync(db.MigrationConnection, """
                BEGIN;
                INSERT INTO ins.site_identity VALUES (1,'11111111-1111-4111-8111-111111111111');
                INSERT INTO rel.software VALUES ('22222222-2222-4222-8222-222222222222','PUB-MIGRATION-S','发布迁移夹具','Vision',NULL,1);
                INSERT INTO ins.processes VALUES ('33333333-3333-4333-8333-333333333333','11111111-1111-4111-8111-111111111111','PUB-MIGRATION-P','迁移工序',1);
                INSERT INTO ins.devices VALUES ('44444444-4444-4444-8444-444444444444','33333333-3333-4333-8333-333333333333','PUB-MIGRATION-D','迁移设备',1);
                INSERT INTO ins.device_software_bindings VALUES ('55555555-5555-4555-8555-555555555555','44444444-4444-4444-8444-444444444444','22222222-2222-4222-8222-222222222222',1,true,true);
                INSERT INTO ins.instances VALUES ('66666666-6666-4666-8666-666666666666','22222222-2222-4222-8222-222222222222','44444444-4444-4444-8444-444444444444','77777777-7777-4777-8777-777777777777','Active',1);
                INSERT INTO rel.releases VALUES ('88888888-8888-4888-8888-888888888888','22222222-2222-4222-8222-222222222222',1,0,0,'Test','Patch','迁移前说明','迁移夹具','99999999-9999-4999-8999-999999999999','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',clock_timestamp(),NULL,NULL,2);
                INSERT INTO pkg.packages VALUES ('99999999-9999-4999-8999-999999999999',gen_random_uuid(),'88888888-8888-4888-8888-888888888888','22222222-2222-4222-8222-222222222222','fixture.zip',123,repeat('a',64),123,repeat('a',64),'Ready',false,3);
                INSERT INTO rel.releases VALUES ('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb','22222222-2222-4222-8222-222222222222',1,0,1,'Disabled','Patch','已停用测试版','迁移夹具','cccccccc-cccc-4ccc-8ccc-cccccccccccc','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',clock_timestamp(),clock_timestamp(),'保留历史',3);
                INSERT INTO pkg.packages VALUES ('cccccccc-cccc-4ccc-8ccc-cccccccccccc',gen_random_uuid(),'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb','22222222-2222-4222-8222-222222222222','fixture-disabled.zip',123,repeat('b',64),123,repeat('b',64),'Ready',true,4);
                INSERT INTO ins.installation_evidence VALUES (gen_random_uuid(),'66666666-6666-4666-8666-666666666666','22222222-2222-4222-8222-222222222222',1,1,'Installed','88888888-8888-4888-8888-888888888888','1.0.0',NULL,clock_timestamp(),'Stopped',repeat('c',64));
                COMMIT;
                """);
            await using (var c=Context(db)) await c.GetService<IMigrator>().MigrateAsync("20261011000100_ReleasePublication");
            var before = await Digest(db);
            var script = db.Runner.GenerateScript(); Assert.Contains("20261012000100_TaskWorkflow", script);
            var directory = Path.Combine(OutboxFixture.Root, "artifacts", "deployment-update"); Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "deployment-update-upgrade.sql"), script);
            var result = await db.Runner.ApplyAsync(default);
            Assert.Equal(10, result.Applied.Count); Assert.Empty(result.Pending); Assert.Equal(before, await Digest(db));
            await using (var c = Context(db))
            {
                var snapshot = c.GetService<Microsoft.EntityFrameworkCore.Infrastructure.IModelRuntimeInitializer>().Initialize(c.GetService<IMigrationsAssembly>().ModelSnapshot!.Model, designTime:true);
                var differences = c.GetService<IMigrationsModelDiffer>().GetDifferences(snapshot.GetRelationalModel(), c.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model.GetRelationalModel());
                Assert.True(differences.Count == 0, string.Join("; ", differences.Select(x => x switch {
                    Microsoft.EntityFrameworkCore.Migrations.Operations.ColumnOperation column => $"{x.GetType().Name}:{column.Table}.{column.Name}:{column.ColumnType}:{column.IsNullable}",
                    Microsoft.EntityFrameworkCore.Migrations.Operations.CreateIndexOperation index => $"{x.GetType().Name}:{index.Table}.{index.Name}",
                    Microsoft.EntityFrameworkCore.Migrations.Operations.RenameIndexOperation index => $"{x.GetType().Name}:{index.Schema}.{index.Table}.{index.Name}->{index.NewName}",
                    _ => x.GetType().Name } )));
            }
            await using (var c = Context(db)) Assert.False(c.Database.HasPendingModelChanges());
            Assert.Equal(2, await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection, "SELECT count(*) FROM rel.releases WHERE \"PublishedAt\" IS NULL AND \"TestEvidenceId\" IS NULL"));
            Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection, "SELECT count(*) FROM rel.releases WHERE \"State\"='Disabled' AND \"PublishedAt\" IS NULL"));
            Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(db.MigrationConnection, "SELECT count(*) FROM pg_indexes WHERE schemaname='tsk' AND indexname='IX_tasks_InstanceId' AND indexdef LIKE '%UNIQUE%'"));
            Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(db.MigrationConnection, "SELECT count(*) FROM pg_indexes WHERE schemaname='tsk' AND indexname='IX_task_dispatches_WorkId_Sequence' AND indexdef LIKE '%UNIQUE%'"));
            Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(db.MigrationConnection, "SELECT count(*) FROM pg_trigger WHERE tgname='TR_release_publication_facts' AND NOT tgisinternal"));
            Assert.False(await PersistenceDatabase.ScalarAsync<bool>(db.ReaderConnection, "SELECT has_table_privilege(current_user,'tsk.tasks','INSERT')"));
            Assert.False(await PersistenceDatabase.ScalarAsync<bool>(db.WriterConnection, "SELECT has_table_privilege(current_user,'tsk.receipts','DELETE')"));
            Assert.False(await PersistenceDatabase.ScalarAsync<bool>(db.WriterConnection, "SELECT has_table_privilege(current_user,'rel.integration_materials','UPDATE')"));
            Assert.Equal(result.Applied, (await db.Runner.ApplyAsync(default)).Applied);
        }
        finally { await db.DisposeAsync(); }
    }
    private static SvmDbContext Context(PersistenceDatabase db) => new(new DbContextOptionsBuilder<SvmDbContext>().UseNpgsql(db.MigrationConnection, p => p.MigrationsHistoryTable("__EFMigrationsHistory", "framework")).Options);
    private static Task<string> Digest(PersistenceDatabase db) => PersistenceDatabase.ScalarAsync<string>(db.MigrationConnection, """
        SELECT md5(string_agg(value,'|' ORDER BY value)) FROM (
          SELECT (to_jsonb(x)-ARRAY['PublishedBy','PublishedEmployeeNo','PublishedAt','TestEvidenceId','PublishReason','PublishConclusion'])::text value FROM rel.releases x
          UNION ALL SELECT to_jsonb(x)::text FROM pkg.packages x
          UNION ALL SELECT to_jsonb(x)::text FROM ins.installation_evidence x
          UNION ALL SELECT to_jsonb(x)::text FROM ins.instances x) facts
        """);
}
