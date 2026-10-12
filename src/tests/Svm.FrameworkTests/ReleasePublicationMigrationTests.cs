using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class ReleasePublicationMigrationTests
{
    [Fact]
    public async Task EightMigrationUpgradePreservesPackagesEvidenceAndUnpublishedDisabledHistory()
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
            var before = await Digest(db);
            var script = db.Runner.GenerateScript(); Assert.Contains("20261011000100_ReleasePublication", script);
            var directory = Path.Combine(OutboxFixture.Root, "artifacts", "release-publication"); Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "release-publication-upgrade.sql"), script);
            var result = await db.Runner.ApplyAsync(default);
            Assert.Equal(9, result.Applied.Count); Assert.Empty(result.Pending); Assert.Equal(before, await Digest(db));
            await using (var c = Context(db)) Assert.False(c.Database.HasPendingModelChanges());
            Assert.Equal(2, await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection, "SELECT count(*) FROM rel.releases WHERE \"PublishedAt\" IS NULL AND \"TestEvidenceId\" IS NULL"));
            Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection, "SELECT count(*) FROM rel.releases WHERE \"State\"='Disabled' AND \"PublishedAt\" IS NULL"));
            Assert.Equal(2, await PersistenceDatabase.ScalarAsync<long>(db.MigrationConnection, "SELECT count(*) FROM pg_constraint WHERE conname IN ('FK_release_publication_evidence','FK_release_publication_person')"));
            Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(db.MigrationConnection, "SELECT count(*) FROM pg_trigger WHERE tgname='TR_release_publication_facts' AND NOT tgisinternal"));
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
