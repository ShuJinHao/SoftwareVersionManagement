using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Svm.EntityFrameworkCore.Framework;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category","Business")]
public sealed class InstanceMigrationTests
{
    [Fact]
    public async Task UpgradeFromSixMigrationsPreservesLedgersAndAddsNoDefaultInstancesOrGrants()
    {
        var db=new PersistenceDatabase();
        try
        {
            await db.CreateAsync(migrate:false);
            await using(var context=new SvmDbContext(new DbContextOptionsBuilder<SvmDbContext>().UseNpgsql(db.MigrationConnection,p=>p.MigrationsHistoryTable("__EFMigrationsHistory","framework")).Options))
                await context.GetService<IMigrator>().MigrateAsync("20261008000100_SiteCatalog");
            const string seed="""
                INSERT INTO ins.site_identity VALUES (1,'11111111-1111-4111-8111-111111111111');
                INSERT INTO rel.software VALUES ('22222222-2222-4222-8222-222222222222','MIGRATION-S','迁移夹具','Vision',NULL,1);
                INSERT INTO ins.processes VALUES ('33333333-3333-4333-8333-333333333333','11111111-1111-4111-8111-111111111111','MIGRATION-P','迁移工序',1);
                INSERT INTO ins.devices VALUES ('44444444-4444-4444-8444-444444444444','33333333-3333-4333-8333-333333333333','MIGRATION-D','迁移设备',1);
                INSERT INTO ins.device_software_bindings VALUES ('55555555-5555-4555-8555-555555555555','44444444-4444-4444-8444-444444444444','22222222-2222-4222-8222-222222222222',1,true,false);
                """;
            await Execute(db.MigrationConnection,seed);
            var before=await Digest(db.MigrationConnection); var script=db.Runner.GenerateScript(); Assert.Contains("20261009000100_InstanceAccess",script);
            PersistenceDatabase.AssertRedacted(script,new NpgsqlConnectionStringBuilder(db.MigrationConnection).Password!);
            var root=new DirectoryInfo(AppContext.BaseDirectory); while(root is not null && !File.Exists(Path.Combine(root.FullName,"AGENTS.md"))) root=root.Parent;
            var folder=Path.Combine(root!.FullName,"artifacts","instance-access"); Directory.CreateDirectory(folder); await File.WriteAllTextAsync(Path.Combine(folder,"instance-access-upgrade.sql"),script);
            var result=await db.Runner.ApplyAsync(default);
            await using(var current=new SvmDbContext(new DbContextOptionsBuilder<SvmDbContext>().UseNpgsql(db.MigrationConnection,p=>p.MigrationsHistoryTable("__EFMigrationsHistory","framework")).Options)) Assert.False(current.Database.HasPendingModelChanges());
            Assert.Equal(7,result.Applied.Count); Assert.Empty(result.Pending); Assert.Equal(before,await Digest(db.MigrationConnection));
            foreach(var table in new[] {"iam.enrollment_grants","iam.recovery_grants","iam.instance_credentials","iam.instance_subjects","iam.registrations","ins.instances","ins.instance_snapshots","ins.installation_evidence","ins.report_stream_receipts"}) Assert.Equal(0,await PersistenceDatabase.ScalarAsync<long>(db.MigrationConnection,"SELECT count(*) FROM "+table));
            Assert.Equal(1,await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection,"SELECT count(*) FROM ins.devices"));
            await Assert.ThrowsAsync<PostgresException>(()=>PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection,"SELECT count(\"SecretHash\") FROM iam.instance_credentials"));
            Assert.Equal(result.Applied,(await db.Runner.ApplyAsync(default)).Applied);
        }
        finally { await db.DisposeAsync(); }
    }
    private static Task<string> Digest(string c)=>PersistenceDatabase.ScalarAsync<string>(c,"""
        SELECT md5(string_agg(value,'|' ORDER BY value)) FROM (
          SELECT row_to_json(x)::text AS value FROM ins.site_identity x UNION ALL SELECT row_to_json(x)::text FROM rel.software x
          UNION ALL SELECT row_to_json(x)::text FROM ins.processes x UNION ALL SELECT row_to_json(x)::text FROM ins.devices x
          UNION ALL SELECT row_to_json(x)::text FROM ins.device_software_bindings x) q
        """);
    private static async Task Execute(string connection,string sql) { await using var c=new NpgsqlConnection(connection); await c.OpenAsync(); await using var q=new NpgsqlCommand(sql,c); await q.ExecuteNonQueryAsync(); }
}
