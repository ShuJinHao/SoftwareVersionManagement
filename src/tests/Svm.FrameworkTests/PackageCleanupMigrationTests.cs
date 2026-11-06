using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Svm.EntityFrameworkCore.Framework;
using Xunit;
namespace Svm.FrameworkTests;

[Trait("Category","Business")]
public sealed class PackageCleanupMigrationTests
{
    [Fact] public async Task SixteenToSeventeenPreservesPackageHistoryAndEnforcesReadinessAndImmutableCleanupFacts()
    {
        await using var db=new PersistenceDatabase(); await db.CreateAsync(false);
        SvmDbContext Context()=>new(new DbContextOptionsBuilder<SvmDbContext>().UseNpgsql(db.MigrationConnection,
            p=>p.MigrationsHistoryTable("__EFMigrationsHistory","framework")).Options);
        await using(var c=Context()) await c.GetService<IMigrator>().MigrateAsync("20261031000100_RollbackCompatibility");
        await PersistenceDatabase.ExecuteAsync(db.MigrationConnection,$"GRANT USAGE ON SCHEMA framework TO {db.ReaderRole}; GRANT SELECT ON framework.\"__EFMigrationsHistory\" TO {db.ReaderRole}");
        await using var probe=new Svm.Dapper.ReadPersistenceProbe(db.ReaderConnection); Assert.False(await probe.CheckAsync(default));
        await PersistenceDatabase.ExecuteAsync(db.MigrationConnection,"""
            BEGIN;
            INSERT INTO iam.users VALUES ('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa','CLEANUP-MIGRATION','虚构迁移人员','unusable-fixture-hash',true,false);
            INSERT INTO rel.software VALUES ('22222222-2222-4222-8222-222222222222','CLEANUP-MIGRATION-S','虚构程序','Vision',NULL,1);
            INSERT INTO rel.releases ("Id","SoftwareId","Major","Minor","Patch","State","ChangeLevel","ChangeSummary","ChangeReason","PackageId","CreatedBy","CreatedAt","Revision")
              VALUES ('88888888-8888-4888-8888-888888888888','22222222-2222-4222-8222-222222222222',1,0,0,'Disabled','Patch','迁移前说明','虚构迁移','99999999-9999-4999-8999-999999999999','aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',clock_timestamp(),2);
            INSERT INTO pkg.packages VALUES ('99999999-9999-4999-8999-999999999999',gen_random_uuid(),'88888888-8888-4888-8888-888888888888','22222222-2222-4222-8222-222222222222','fixture.zip',123,repeat('a',64),123,repeat('a',64),'Ready',true,3);
            INSERT INTO pkg.download_sessions ("Id","PackageId","NodeId","WorkerGeneration","SubjectId","ActorKind","StartedAt","State")
              VALUES ('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb','99999999-9999-4999-8999-999999999999','node-a',gen_random_uuid(),'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa','Human',clock_timestamp(),'Open');
            COMMIT;
            """);
        var before=await Digest(db); var result=await db.Runner.ApplyAsync(default);
        Assert.Equal(PersistenceDatabase.CurrentMigrationCount,result.Applied.Count); Assert.True(await probe.CheckAsync(default));
        Assert.Equal(before,await Digest(db)); Assert.Equal(0,await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection,"SELECT count(*) FROM pkg.cleanup_intents"));
        Assert.False(await PersistenceDatabase.ScalarAsync<bool>(db.WriterConnection,"SELECT has_table_privilege(current_user,'pkg.cleanup_intents','DELETE') OR has_table_privilege(current_user,'pkg.download_stop_evidence','UPDATE')"));
        Assert.False(await PersistenceDatabase.ScalarAsync<bool>(db.ReaderConnection,"SELECT has_table_privilege(current_user,'pkg.download_stop_evidence','INSERT')"));
        Assert.Equal(0,await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection,"SELECT count(*) FROM iam.permissions WHERE \"Operation\"='package.clean'"));
        await PersistenceDatabase.ExecuteAsync(db.WriterConnection,"UPDATE pkg.packages SET \"State\"='Cleaning'");
        await Assert.ThrowsAsync<PostgresException>(()=>PersistenceDatabase.ExecuteAsync(db.WriterConnection,"UPDATE pkg.packages SET \"State\"='Ready'"));
        await PersistenceDatabase.ExecuteAsync(db.WriterConnection,"UPDATE pkg.packages SET \"State\"='Cleaned'");
        await Assert.ThrowsAsync<PostgresException>(()=>PersistenceDatabase.ExecuteAsync(db.WriterConnection,"UPDATE pkg.packages SET \"Disabled\"=false"));
        await using(var c=Context())
        {
            var snapshot=c.GetService<IModelRuntimeInitializer>().Initialize(c.GetService<IMigrationsAssembly>().ModelSnapshot!.Model,designTime:true);
            var diff=c.GetService<IMigrationsModelDiffer>().GetDifferences(snapshot.GetRelationalModel(),c.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model.GetRelationalModel());
            Assert.True(diff.Count==0,string.Join(";",diff.Select(d=>System.Text.Json.JsonSerializer.Serialize(d,d.GetType()))));
        }
        await using(var c=Context()) Assert.False(c.Database.HasPendingModelChanges());
        var path=Path.Combine(OutboxFixture.Root,"artifacts/package-cleanup"); Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path,"upgrade.sql"),db.Runner.GenerateScript());
        Assert.Equal(result.Applied,(await db.Runner.ApplyAsync(default)).Applied);
    }
    private static Task<string> Digest(PersistenceDatabase db)=>PersistenceDatabase.ScalarAsync<string>(db.MigrationConnection,"""
        SELECT md5(string_agg(value,'|' ORDER BY value)) FROM (
          SELECT to_jsonb(x)::text value FROM rel.releases x UNION ALL SELECT to_jsonb(x)::text FROM pkg.packages x
          UNION ALL SELECT to_jsonb(x)::text FROM pkg.download_sessions x) facts
        """);
}
