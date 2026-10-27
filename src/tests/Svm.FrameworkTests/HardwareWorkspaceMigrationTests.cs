using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class HardwareWorkspaceMigrationTests
{
    [Fact]
    public async Task ReadinessRejectsElevenMigrationsAndAcceptsHardwareUpgradeWithoutWriting()
    {
        await using var db = new PersistenceDatabase(); await db.CreateAsync(false);
        await using (var context = Context(db)) await context.GetService<IMigrator>().MigrateAsync("20261013000100_GitLabReleaseImport");
        await PersistenceDatabase.ExecuteAsync(db.MigrationConnection, $"GRANT USAGE ON SCHEMA framework TO {db.ReaderRole}; GRANT SELECT ON framework.\"__EFMigrationsHistory\" TO {db.ReaderRole}");
        await using var probe = new Svm.Dapper.ReadPersistenceProbe(db.ReaderConnection);
        Assert.False(await probe.CheckAsync(default));
        Assert.Equal(11, await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection, "SELECT count(*) FROM framework.\"__EFMigrationsHistory\""));
        await db.Runner.ApplyAsync(default);
        Assert.True(await probe.CheckAsync(default));
        Assert.True(await probe.CheckAsync(default));
        Assert.Equal(13, await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection, "SELECT count(*) FROM framework.\"__EFMigrationsHistory\""));
        Assert.Equal(0, await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection, "SELECT count(*) FROM ins.hardware"));
        Assert.False(await PersistenceDatabase.ScalarAsync<bool>(db.ReaderConnection, "SELECT has_table_privilege(current_user, 'framework.\"__EFMigrationsHistory\"', 'INSERT')"));
    }

    [Fact]
    public async Task ElevenMigrationUpgradePreservesUnboundInstancesAndMatchesFrozenModelWithoutDefaultHardware()
    {
        await using var db = new PersistenceDatabase(); await db.CreateAsync(false);
        await using (var context = Context(db)) await context.GetService<IMigrator>().MigrateAsync("20261013000100_GitLabReleaseImport");
        await PersistenceDatabase.ExecuteAsync(db.MigrationConnection, """
            INSERT INTO ins.site_identity VALUES (1,'11111111-1111-4111-8111-111111111111');
            INSERT INTO rel.software VALUES ('22222222-2222-4222-8222-222222222222','SW-000001','历史软件夹具','Vision',NULL,1);
            INSERT INTO ins.processes VALUES ('33333333-3333-4333-8333-333333333333','11111111-1111-4111-8111-111111111111','PR-000001','历史工序夹具',1);
            INSERT INTO ins.devices VALUES ('44444444-4444-4444-8444-444444444444','33333333-3333-4333-8333-333333333333','EQ-000001','历史设备夹具',1);
            INSERT INTO ins.device_software_bindings VALUES ('55555555-5555-4555-8555-555555555555','44444444-4444-4444-8444-444444444444','22222222-2222-4222-8222-222222222222',1,true,true);
            INSERT INTO ins.instances VALUES ('66666666-6666-4666-8666-666666666666','22222222-2222-4222-8222-222222222222','44444444-4444-4444-8444-444444444444','77777777-7777-4777-8777-777777777777','Active',1);
            """);
        var before = await Digest(db); var script = db.Runner.GenerateScript();
        Assert.Contains("20261021000100_HardwareWorkspace", script);
        var directory = Path.Combine(OutboxFixture.Root,"artifacts","hardware-workspace"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory,"hardware-workspace-upgrade.sql"),script);
        var result = await db.Runner.ApplyAsync(default); Assert.Equal(14,result.Applied.Count); Assert.Empty(result.Pending);
        Assert.Equal(before,await Digest(db));
        Assert.Equal(0,await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection,"SELECT count(*) FROM ins.hardware"));
        Assert.Equal(0,await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection,"SELECT count(*) FROM ins.hardware_software_bindings"));
        Assert.Equal(1,await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection,"SELECT count(*) FROM ins.instances WHERE \"HardwareBindingId\" IS NULL"));
        Assert.True(await PersistenceDatabase.ScalarAsync<bool>(db.WriterConnection,"SELECT has_sequence_privilege(current_user,'ins.device_no_seq','USAGE')"));
        Assert.False(await PersistenceDatabase.ScalarAsync<bool>(db.ReaderConnection,"SELECT has_sequence_privilege(current_user,'ins.device_no_seq','USAGE')"));
        Assert.False(await PersistenceDatabase.ScalarAsync<bool>(db.WriterConnection,"SELECT has_table_privilege(current_user,'ins.hardware','DELETE')"));
        Assert.True(await PersistenceDatabase.ScalarAsync<bool>(db.ReaderConnection,"SELECT has_column_privilege(current_user,'iam.enrollment_grants','HardwareBindingId','SELECT')"));
        await using (var context = Context(db))
        {
            var snapshot=context.GetService<IModelRuntimeInitializer>().Initialize(context.GetService<IMigrationsAssembly>().ModelSnapshot!.Model,designTime:true);
            var differences=context.GetService<IMigrationsModelDiffer>().GetDifferences(snapshot.GetRelationalModel(),context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model.GetRelationalModel());
            Assert.True(differences.Count==0,string.Join("; ",differences.Select(x=>x.GetType().Name+":"+System.Text.Json.JsonSerializer.Serialize(x,x.GetType()))));
        }
        await using (var freshContext = Context(db)) Assert.False(freshContext.Database.HasPendingModelChanges());
        Assert.Equal(result.Applied,(await db.Runner.ApplyAsync(default)).Applied);
    }
    private static SvmDbContext Context(PersistenceDatabase db) => new(new DbContextOptionsBuilder<SvmDbContext>().UseNpgsql(db.MigrationConnection,p=>p.MigrationsHistoryTable("__EFMigrationsHistory","framework")).Options);
    private static Task<string> Digest(PersistenceDatabase db)=>PersistenceDatabase.ScalarAsync<string>(db.MigrationConnection,"""
        SELECT md5(string_agg(value,'|' ORDER BY value)) FROM (
          SELECT to_jsonb(x)::text AS value FROM rel.software x UNION ALL SELECT to_jsonb(x)::text FROM ins.processes x
          UNION ALL SELECT (to_jsonb(x)-'AssetNumber')::text FROM ins.devices x
          UNION ALL SELECT (to_jsonb(x)-'HardwareBindingId')::text FROM ins.instances x
          UNION ALL SELECT to_jsonb(x)::text FROM ins.device_software_bindings x) rows
        """);
}
