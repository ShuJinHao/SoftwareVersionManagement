using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Svm.EntityFrameworkCore.Framework;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class RollbackMigrationTests
{
    [Fact]
    public async Task FifteenToSixteenPreservesUpdateAndPermitFactsAndRequiresTheNewReadinessBaseline()
    {
        await using var db = new PersistenceDatabase(); await db.CreateAsync(false);
        SvmDbContext Context() => new(new DbContextOptionsBuilder<SvmDbContext>().UseNpgsql(db.MigrationConnection,
            p => p.MigrationsHistoryTable("__EFMigrationsHistory", "framework")).Options);
        await using (var c = Context()) await c.GetService<IMigrator>().MigrateAsync("20261028000100_PlcArchives");
        await PersistenceDatabase.ExecuteAsync(db.MigrationConnection, $"GRANT USAGE ON SCHEMA framework TO {db.ReaderRole}; GRANT SELECT ON framework.\"__EFMigrationsHistory\" TO {db.ReaderRole}");
        await using var probe = new Svm.Dapper.ReadPersistenceProbe(db.ReaderConnection);
        Assert.False(await probe.CheckAsync(default));
        await PersistenceDatabase.ExecuteAsync(db.MigrationConnection, """
            BEGIN;
            INSERT INTO iam.users VALUES ('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa','ROLLBACK-MIGRATION','虚构迁移人员','unusable-fixture-hash',true,false);
            INSERT INTO ins.site_identity VALUES (1,'11111111-1111-4111-8111-111111111111');
            INSERT INTO rel.software VALUES ('22222222-2222-4222-8222-222222222222','ROLLBACK-MIGRATION-S','虚构程序','Vision',NULL,1);
            INSERT INTO ins.processes VALUES ('33333333-3333-4333-8333-333333333333','11111111-1111-4111-8111-111111111111','ROLLBACK-MIGRATION-P','虚构工序',1);
            INSERT INTO ins.devices ("Id","ProcessId","DeviceNo","Name","Revision") VALUES ('44444444-4444-4444-8444-444444444444','33333333-3333-4333-8333-333333333333','ROLLBACK-MIGRATION-D','虚构设备',1);
            INSERT INTO ins.device_software_bindings VALUES ('55555555-5555-4555-8555-555555555555','44444444-4444-4444-8444-444444444444','22222222-2222-4222-8222-222222222222',1,true,true);
            INSERT INTO ins.instances ("Id","SoftwareId","DeviceId","InstallationKey","Lifecycle","Revision") VALUES ('66666666-6666-4666-8666-666666666666','22222222-2222-4222-8222-222222222222','44444444-4444-4444-8444-444444444444','77777777-7777-4777-8777-777777777777','Active',1);
            INSERT INTO rel.releases ("Id","SoftwareId","Major","Minor","Patch","State","ChangeLevel","ChangeSummary","ChangeReason","PackageId","CreatedBy","CreatedAt","Revision") VALUES ('88888888-8888-4888-8888-888888888888','22222222-2222-4222-8222-222222222222',1,0,0,'Test','Patch','迁移前说明','虚构迁移','99999999-9999-4999-8999-999999999999','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',clock_timestamp(),2);
            INSERT INTO pkg.packages VALUES ('99999999-9999-4999-8999-999999999999',gen_random_uuid(),'88888888-8888-4888-8888-888888888888','22222222-2222-4222-8222-222222222222','fixture.zip',123,repeat('a',64),123,repeat('a',64),'Ready',false,3);
            INSERT INTO ins.installation_evidence VALUES ('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb','66666666-6666-4666-8666-666666666666','22222222-2222-4222-8222-222222222222',1,1,'Installed','88888888-8888-4888-8888-888888888888','1.0.0',NULL,clock_timestamp(),'Stopped',repeat('b',64));
            UPDATE rel.releases SET "State"='Formal',"PublishedBy"='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',"PublishedEmployeeNo"='ROLLBACK-MIGRATION',"PublishedAt"=clock_timestamp(),"TestEvidenceId"='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',"PublishReason"='虚构迁移',"PublishConclusion"='虚构历史证据';
            INSERT INTO tsk.target_selections VALUES ('cccccccc-cccc-4ccc-8ccc-cccccccccccc','22222222-2222-4222-8222-222222222222','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa','Explicit',NULL,'Sealed',1,1,clock_timestamp(),3);
            INSERT INTO tsk.deployments VALUES ('dddddddd-dddd-4ddd-8ddd-dddddddddddd','22222222-2222-4222-8222-222222222222','cccccccc-cccc-4ccc-8ccc-cccccccccccc','88888888-8888-4888-8888-888888888888',clock_timestamp(),true,'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',NULL,'虚构历史更新','Running',clock_timestamp(),clock_timestamp()+interval '1 hour',1,1,300,1,1,1,0,'',false,4);
            INSERT INTO tsk.tasks ("Id","DeploymentId","InstanceId","TargetReleaseId","CreatedAt","State","NotBefore","LatestStart","IsDeferred","LastProgressSequence","Revision") VALUES ('eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee','dddddddd-dddd-4ddd-8ddd-dddddddddddd','66666666-6666-4666-8666-666666666666','88888888-8888-4888-8888-888888888888',clock_timestamp(),'AwaitingResult',clock_timestamp(),clock_timestamp()+interval '1 hour',false,0,2);
            INSERT INTO tsk.attempts VALUES ('ffffffff-ffff-4fff-8fff-ffffffffffff','eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee','99999999-9999-4999-8999-999999999999',repeat('a',64),clock_timestamp(),clock_timestamp()+interval '1 hour');
            UPDATE tsk.tasks SET "AttemptId"='ffffffff-ffff-4fff-8fff-ffffffffffff',"ResponseDeadlineAt"=clock_timestamp()+interval '5 minutes';
            COMMIT;
            """);
        var before = await Digest(db);
        var result = await db.Runner.ApplyAsync(default);
        Assert.Equal(PersistenceDatabase.CurrentMigrationCount, result.Applied.Count);
        Assert.True(await probe.CheckAsync(default)); Assert.Equal(before, await Digest(db));
        Assert.Equal("Update", await PersistenceDatabase.ScalarAsync<string>(db.ReaderConnection,"SELECT \"Kind\" FROM tsk.deployments"));
        Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection,"SELECT count(*) FROM tsk.attempts WHERE \"CompatibilityDeclarationId\" IS NULL AND \"StartAuthorizedAt\" IS NOT NULL"));
        Assert.False(await PersistenceDatabase.ScalarAsync<bool>(db.WriterConnection,"SELECT has_table_privilege(current_user,'rel.compatibility_declarations','UPDATE') OR has_table_privilege(current_user,'rel.compatibility_declarations','DELETE')"));
        Assert.False(await PersistenceDatabase.ScalarAsync<bool>(db.ReaderConnection,"SELECT has_table_privilege(current_user,'rel.compatibility_declarations','INSERT')"));
        Assert.Equal(0, await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection,"SELECT count(*) FROM iam.permissions WHERE \"Operation\"='deployment.rollback'"));
        await Assert.ThrowsAsync<PostgresException>(() => PersistenceDatabase.ExecuteAsync(db.WriterConnection,"UPDATE tsk.deployments SET \"Kind\"='Rollback'"));
        await Assert.ThrowsAsync<PostgresException>(() => PersistenceDatabase.ExecuteAsync(db.WriterConnection,"UPDATE tsk.attempts SET \"Sha256\"=repeat('b',64)"));
        await using (var c = Context())
        {
            var snapshot = c.GetService<IModelRuntimeInitializer>().Initialize(c.GetService<IMigrationsAssembly>().ModelSnapshot!.Model,designTime:true);
            var diff = c.GetService<IMigrationsModelDiffer>().GetDifferences(snapshot.GetRelationalModel(),c.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model.GetRelationalModel());
            Assert.True(diff.Count==0,string.Join(";",diff.Select(d=>System.Text.Json.JsonSerializer.Serialize(d,d.GetType()))));
        }
        await using (var c = Context()) Assert.False(c.Database.HasPendingModelChanges());
        var directory = Path.Combine(OutboxFixture.Root,"artifacts/rollback-compatibility"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory,"upgrade.sql"),db.Runner.GenerateScript());
        Assert.Equal(result.Applied,(await db.Runner.ApplyAsync(default)).Applied);
    }

    private static Task<string> Digest(PersistenceDatabase db) => PersistenceDatabase.ScalarAsync<string>(db.MigrationConnection,"""
        SELECT md5(string_agg(value,'|' ORDER BY value)) FROM (
          SELECT (to_jsonb(x)-'Kind')::text value FROM tsk.deployments x
          UNION ALL SELECT (to_jsonb(x)-ARRAY['AdmissionCompatibilityId','AdmissionCompatibilityJson'])::text FROM tsk.tasks x
          UNION ALL SELECT (to_jsonb(x)-ARRAY['CompatibilityDeclarationId','CompatibilityJson'])::text FROM tsk.attempts x
          UNION ALL SELECT to_jsonb(x)::text FROM rel.releases x
          UNION ALL SELECT to_jsonb(x)::text FROM ins.installation_evidence x) facts
        """);
}
