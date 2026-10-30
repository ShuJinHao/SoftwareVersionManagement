using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;
using Xunit;
namespace Svm.FrameworkTests;

[Trait("Category","Business")]
public sealed class PlcMigrationTests
{
    [Fact]
    public async Task FourteenToFifteenPreservesHistoryRestrictsSecretsAndEnablesReadiness()
    {
        await using var db=new PersistenceDatabase();await db.CreateAsync(false);
        SvmDbContext Context()=>new(new DbContextOptionsBuilder<SvmDbContext>().UseNpgsql(db.MigrationConnection,p=>p.MigrationsHistoryTable("__EFMigrationsHistory","framework")).Options);
        await using(var c=Context())await c.GetService<IMigrator>().MigrateAsync("20261025000100_GitLabSourceManagement");
        await PersistenceDatabase.ExecuteAsync(db.MigrationConnection,$"GRANT USAGE ON SCHEMA framework TO {db.ReaderRole}; GRANT SELECT ON framework.\"__EFMigrationsHistory\" TO {db.ReaderRole}");
        await using var probe=new Svm.Dapper.ReadPersistenceProbe(db.ReaderConnection);Assert.False(await probe.CheckAsync(default));
        await PersistenceDatabase.ExecuteAsync(db.MigrationConnection,"""
            INSERT INTO rel.software VALUES ('11111111-1111-4111-8111-111111111111','PLC-MIGRATION-OLD','Old Vision','Vision',NULL,1);
            """);
        var before=await PersistenceDatabase.ScalarAsync<string>(db.MigrationConnection,"SELECT row_to_json(s)::text FROM rel.software s");
        var result=await db.Runner.ApplyAsync(default);Assert.Equal(15,result.Applied.Count);Assert.True(await probe.CheckAsync(default));
        Assert.Equal(before,await PersistenceDatabase.ScalarAsync<string>(db.ReaderConnection,"SELECT row_to_json(s)::text FROM rel.software s"));
        Assert.False(await PersistenceDatabase.ScalarAsync<bool>(db.ReaderConnection,"SELECT has_column_privilege(current_user,'iam.plc_upload_credentials','SecretHash','SELECT')"));
        Assert.True(await PersistenceDatabase.ScalarAsync<bool>(db.ReaderConnection,"SELECT has_column_privilege(current_user,'iam.plc_upload_credentials','ScopeJson','SELECT')"));
        Assert.False(await PersistenceDatabase.ScalarAsync<bool>(db.WriterConnection,"SELECT has_table_privilege(current_user,'ins.plc_usage_records','UPDATE')"));
        Assert.Equal(0,await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection,"SELECT count(*) FROM iam.permissions WHERE \"Operation\" LIKE 'plc.%'"));
        await using(var c=Context())
        {
            var snapshot=c.GetService<IModelRuntimeInitializer>().Initialize(c.GetService<IMigrationsAssembly>().ModelSnapshot!.Model,designTime:true);
            var diff=c.GetService<IMigrationsModelDiffer>().GetDifferences(snapshot.GetRelationalModel(),c.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model.GetRelationalModel());
            Assert.True(diff.Count==0,string.Join(";",diff.Select(d=>System.Text.Json.JsonSerializer.Serialize(d,d.GetType()))));
        }
        var path=Path.Combine(OutboxFixture.Root,"artifacts/plc-archives");Directory.CreateDirectory(path);await File.WriteAllTextAsync(Path.Combine(path,"upgrade.sql"),db.Runner.GenerateScript());
        Assert.Equal(result.Applied,(await db.Runner.ApplyAsync(default)).Applied);Assert.True(await probe.CheckAsync(default));
    }
}
