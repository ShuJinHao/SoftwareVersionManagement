using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;
using Svm.EntityFrameworkCore.Migrations;
using Svm.Services.Contracts.Framework;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class PersistenceMigrationTests
{
    private const string SchemaCount = "SELECT count(*) FROM pg_namespace WHERE nspname IN ('iam','rel','pkg','ins','tsk','aud','framework')";

    [Fact]
    public async Task ExistingInitialMigrationUpgradesToSameTablesAndPrivilegesAsFreshDatabase()
    {
        var upgraded = new PersistenceDatabase();
        var fresh = new PersistenceDatabase();
        try
        {
            await upgraded.CreateAsync(migrate: false);
            await using (var context = new SvmDbContext(new DbContextOptionsBuilder<SvmDbContext>()
                .UseNpgsql(upgraded.MigrationConnection, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "framework")).Options))
                await context.GetService<IMigrator>().MigrateAsync("20260930000100_InitialSchemas");
            Assert.Single((await upgraded.Runner.StatusAsync(default)).Applied);
            Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(upgraded.MigrationConnection,
                "SELECT count(*) FROM pg_tables WHERE schemaname IN ('iam','rel','pkg','ins','tsk','aud','framework')"));
            await upgraded.Runner.ApplyAsync(default);
            await fresh.CreateAsync(migrate: true);
            await using (var context = new SvmDbContext(new DbContextOptionsBuilder<SvmDbContext>()
                .UseNpgsql(fresh.MigrationConnection, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "framework")).Options))
                Assert.False(context.Database.HasPendingModelChanges());
            const string shape = """
                SELECT string_agg(table_schema || '.' || table_name || ':' || column_name || ':' || data_type || ':' || is_nullable, ',' ORDER BY table_schema,table_name,ordinal_position)
                FROM information_schema.columns WHERE table_schema IN ('iam','rel','pkg','ins','tsk','aud','framework')
                """;
            Assert.Equal(await PersistenceDatabase.ScalarAsync<string>(fresh.MigrationConnection, shape),
                await PersistenceDatabase.ScalarAsync<string>(upgraded.MigrationConnection, shape));
            foreach (var database in new[] { upgraded, fresh })
            {
                Assert.True(await PersistenceDatabase.ScalarAsync<bool>(database.WriterConnection,
                    "SELECT has_table_privilege(current_user,'framework.data_protection_keys','INSERT') AND NOT has_table_privilege(current_user,'framework.data_protection_keys','DELETE') AND NOT has_table_privilege(current_user,'aud.events','UPDATE')"));
                Assert.Equal(0, await PersistenceDatabase.ScalarAsync<long>(database.ReaderConnection, "SELECT count(*) FROM iam.users"));
            }
        }
        finally { await upgraded.DisposeAsync(); await fresh.DisposeAsync(); }
    }

    [Fact]
    public async Task PersonnelDatabaseUpgradePreservesExistingUsersSessionsAndAuditBytes()
    {
        var database = new PersistenceDatabase();
        try
        {
            await database.CreateAsync(migrate: false);
            await using (var context = new SvmDbContext(new DbContextOptionsBuilder<SvmDbContext>()
                .UseNpgsql(database.MigrationConnection, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "framework")).Options))
                await context.GetService<IMigrator>().MigrateAsync("20261001000100_PersonnelSessions");
            var user = Guid.NewGuid(); var operation = Guid.NewGuid();
            await PersistenceDatabase.ExecuteAsync(database.MigrationConnection, $"""
                INSERT INTO iam.users VALUES ('{user}','UPGRADE-FIXTURE','Preserved account','fixture-hash',true,false);
                INSERT INTO iam.subject_guards VALUES ('{user}',1);
                INSERT INTO iam.sessions VALUES ('{Guid.NewGuid()}','{user}','fixture-secret-hash',clock_timestamp()+interval '1 hour',NULL);
                INSERT INTO aud.events VALUES ('{Guid.NewGuid()}','{operation}','{user}','Human',NULL,NULL,'fixture.previous',NULL,'Completed','Preserved evidence','fixture-correlation',clock_timestamp());
                """);
            const string previous = """
                SELECT json_build_object('users',(SELECT json_agg(u ORDER BY "Id") FROM iam.users u),
                  'sessions',(SELECT json_agg(s ORDER BY "Id") FROM iam.sessions s),
                  'guards',(SELECT json_agg(g ORDER BY "SubjectId") FROM iam.subject_guards g),
                  'audit',(SELECT json_agg(a ORDER BY "Id") FROM aud.events a))::text
                """;
            var before = await PersistenceDatabase.ScalarAsync<string>(database.MigrationConnection, previous);
            var pending = await database.Runner.StatusAsync(default);
            Assert.Equal(new[] { "20261006000100_OperationResults", "20261007000100_BusOutbox", "20261007000200_PersonnelAdministrationPermissions", "20261008000100_SiteCatalog" }, pending.Pending);
            await database.Runner.ApplyAsync(default);
            Assert.Equal(before, await PersistenceDatabase.ScalarAsync<string>(database.ReaderConnection, previous));
            foreach (var schema in new[] { "iam", "rel", "pkg", "ins", "tsk", "aud" })
                Assert.Equal(0, await PersistenceDatabase.ScalarAsync<long>(database.ReaderConnection, $"SELECT count(*) FROM {schema}.operation_results"));
        }
        finally { await database.DisposeAsync(); }
    }

    [Fact]
    public async Task BusOutboxUpgradePreservesPreviouslyRetainedOperationResultsAndAudit()
    {
        var database = new PersistenceDatabase();
        try
        {
            await database.CreateAsync(migrate: false);
            await using (var context = new SvmDbContext(new DbContextOptionsBuilder<SvmDbContext>()
                .UseNpgsql(database.MigrationConnection, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "framework")).Options))
                await context.GetService<IMigrator>().MigrateAsync("20261006000100_OperationResults");
            var operation = Guid.NewGuid();
            await PersistenceDatabase.ExecuteAsync(database.MigrationConnection, $"""
                INSERT INTO pkg.operation_results ("ActorKind","SubjectId","Operation","IdempotencyKey","RequestDigest","OperationId","Status","ResourceId","CompletedAt")
                VALUES (2,'{Guid.NewGuid()}','fixture.retained','{Guid.NewGuid()}','{new string('a',64)}','{operation}',2,'{Guid.NewGuid()}',clock_timestamp());
                INSERT INTO aud.events VALUES ('{Guid.NewGuid()}','{operation}',NULL,'Human',NULL,NULL,'fixture.retained',NULL,'Completed','Preserved evidence','fixture',clock_timestamp());
                """);
            const string rows = "SELECT json_build_object('results',(SELECT json_agg(r) FROM pkg.operation_results r),'audit',(SELECT json_agg(a) FROM aud.events a))::text";
            var before = await PersistenceDatabase.ScalarAsync<string>(database.MigrationConnection, rows);
            Assert.Equal(new[] { "20261007000100_BusOutbox", "20261007000200_PersonnelAdministrationPermissions", "20261008000100_SiteCatalog" }, (await database.Runner.StatusAsync(default)).Pending);
            await database.Runner.ApplyAsync(default);
            Assert.Equal(before, await PersistenceDatabase.ScalarAsync<string>(database.ReaderConnection, rows));
            Assert.Equal(0, await PersistenceDatabase.ScalarAsync<long>(database.ReaderConnection,"SELECT count(*) FROM framework.\"InboxState\""));
        }
        finally { await database.DisposeAsync(); }
    }

    [Fact]
    public async Task EmptyDatabaseStatusAndScriptDoNotMutateAndApplyCanBeRepeated()
    {
        var database = new PersistenceDatabase();
        try
        {
            await database.CreateAsync(migrate: false);
            var before = await database.Runner.StatusAsync(default);
            Assert.Empty(before.Applied);
            Assert.Equal(new[] { "20260930000100_InitialSchemas", "20261001000100_PersonnelSessions", "20261006000100_OperationResults", "20261007000100_BusOutbox", "20261007000200_PersonnelAdministrationPermissions", "20261008000100_SiteCatalog" }, before.Pending);
            var script = database.Runner.GenerateScript();
            Assert.Contains("pg_try_advisory_lock", script);
            Assert.Contains("SVM migration role configuration invalid", script);
            PersistenceDatabase.AssertRedacted(script, new NpgsqlConnectionStringBuilder(database.MigrationConnection).Password!);
            Assert.Equal(0, await PersistenceDatabase.ScalarAsync<long>(database.MigrationConnection, SchemaCount));
            var first = await database.Runner.ApplyAsync(default);
            var second = await database.Runner.ApplyAsync(default);
            Assert.Empty(first.Pending);
            Assert.Equal(first.Applied, second.Applied);
            Assert.Equal(6, second.Applied.Count);
            Assert.Equal(7, await PersistenceDatabase.ScalarAsync<long>(database.MigrationConnection, SchemaCount));
            Assert.Equal(24, await PersistenceDatabase.ScalarAsync<long>(database.MigrationConnection,
                "SELECT count(*) FROM pg_tables WHERE schemaname IN ('iam','rel','pkg','ins','tsk','aud','framework')"));
            Assert.Equal(6, await PersistenceDatabase.ScalarAsync<long>(database.MigrationConnection,
                "SELECT count(*) FROM framework.\"__EFMigrationsHistory\""));
        }
        finally { await database.DisposeAsync(); }
    }

    [Fact]
    public async Task AdministrationDataMigrationPreservesAccountsGrantsSessionsAuditAndResults()
    {
        await using var database = new PersistenceDatabase();
        await database.CreateAsync(migrate: false);
        await using (var context = new SvmDbContext(new DbContextOptionsBuilder<SvmDbContext>()
            .UseNpgsql(database.MigrationConnection, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "framework")).Options))
            await context.GetService<IMigrator>().MigrateAsync("20261007000100_BusOutbox");
        var subject = Guid.NewGuid(); var operation = Guid.NewGuid();
        await PersistenceDatabase.ExecuteAsync(database.MigrationConnection, $"""
            INSERT INTO iam.permission_catalog VALUES ('identity.manage',true);
            INSERT INTO iam.users VALUES ('{subject}','MIGRATION-FIXTURE','既有人员','fixture-hash',true,true);
            INSERT INTO iam.subject_guards VALUES ('{subject}',7);
            INSERT INTO iam.permissions VALUES ('{Guid.NewGuid()}','{subject}',NULL,'identity.manage');
            INSERT INTO iam.sessions VALUES ('{Guid.NewGuid()}','{subject}','{new string('a',64)}',clock_timestamp()+interval '1 hour',NULL);
            INSERT INTO aud.events VALUES ('{Guid.NewGuid()}','{operation}','{subject}','Human','MIGRATION-FIXTURE','既有人员','fixture.retained','{subject}','succeeded','retained','fixture',clock_timestamp());
            INSERT INTO iam.operation_results ("ActorKind","SubjectId","Operation","IdempotencyKey","RequestDigest","OperationId","Status","ResourceId","CompletedAt")
            VALUES (2,'{subject}','fixture.retained','{Guid.NewGuid()}','{new string('b',64)}','{operation}',2,'{subject}',clock_timestamp());
            """);
        const string data = """
            SELECT json_build_object('users',(SELECT json_agg(u) FROM iam.users u),'grants',(SELECT json_agg(p) FROM iam.permissions p),
            'guards',(SELECT json_agg(g) FROM iam.subject_guards g),'sessions',(SELECT json_agg(s) FROM iam.sessions s),
            'audit',(SELECT json_agg(a) FROM aud.events a),'results',(SELECT json_agg(r) FROM iam.operation_results r))::text
            """;
        var before = await PersistenceDatabase.ScalarAsync<string>(database.MigrationConnection, data);
        await database.Runner.ApplyAsync(default);
        Assert.Equal(before, await PersistenceDatabase.ScalarAsync<string>(database.ReaderConnection, data));
        Assert.Equal(2, await PersistenceDatabase.ScalarAsync<long>(database.ReaderConnection,
            "SELECT count(*) FROM iam.permission_catalog WHERE \"Operation\" IN ('asset.read','asset.manage') AND \"Global\""));
        Assert.Equal(1, await PersistenceDatabase.ScalarAsync<long>(database.ReaderConnection, "SELECT count(*) FROM iam.permissions"));
    }

    [Fact]
    public async Task SiteCatalogUpgradePreservesFiveMigrationDataAndCreatesNoDefaultLedger()
    {
        await using var db = new PersistenceDatabase(); await db.CreateAsync(migrate: false);
        await using (var context = new SvmDbContext(new DbContextOptionsBuilder<SvmDbContext>().UseNpgsql(db.MigrationConnection,
            options => options.MigrationsHistoryTable("__EFMigrationsHistory", "framework")).Options))
            await context.GetService<IMigrator>().MigrateAsync("20261007000200_PersonnelAdministrationPermissions");
        var subject = Guid.NewGuid(); var operation = Guid.NewGuid(); var outbox = Guid.NewGuid();
        await PersistenceDatabase.ExecuteAsync(db.MigrationConnection, $"""
            INSERT INTO iam.permission_catalog VALUES ('identity.manage',true);
            INSERT INTO iam.users VALUES ('{subject}','CATALOG-MIGRATION','既有人员','fixture-hash',true,false);
            INSERT INTO iam.subject_guards VALUES ('{subject}',11);
            INSERT INTO iam.permissions VALUES ('{Guid.NewGuid()}','{subject}',NULL,'identity.manage');
            INSERT INTO iam.sessions VALUES ('{Guid.NewGuid()}','{subject}','{new string('a',64)}',clock_timestamp()+interval '1 hour',NULL);
            INSERT INTO aud.events VALUES ('{Guid.NewGuid()}','{operation}','{subject}','Human','CATALOG-MIGRATION','既有人员','fixture.retained','{subject}','succeeded','retained','fixture',clock_timestamp());
            INSERT INTO iam.operation_results ("ActorKind","SubjectId","Operation","IdempotencyKey","RequestDigest","OperationId","Status","ResourceId","CompletedAt")
              VALUES (2,'{subject}','fixture.retained','{Guid.NewGuid()}','{new string('b',64)}','{operation}',2,'{subject}',clock_timestamp());
            INSERT INTO framework."OutboxState" ("OutboxId","LockId","Created") VALUES ('{outbox}','{Guid.NewGuid()}',clock_timestamp());
            INSERT INTO framework."InboxState" ("MessageId","ConsumerId","LockId","Received","ReceiveCount") VALUES ('{Guid.NewGuid()}','{Guid.NewGuid()}','{Guid.NewGuid()}',clock_timestamp(),1);
            """);
        const string retained = """
            SELECT json_build_object('users',(SELECT json_agg(u) FROM iam.users u),'guards',(SELECT json_agg(g) FROM iam.subject_guards g),
              'grants',(SELECT json_agg(p) FROM iam.permissions p),'sessions',(SELECT json_agg(s) FROM iam.sessions s),
              'audit',(SELECT json_agg(a) FROM aud.events a),'results',(SELECT json_agg(r) FROM iam.operation_results r),
              'outbox',(SELECT json_agg(o) FROM framework."OutboxState" o),'inbox',(SELECT json_agg(i) FROM framework."InboxState" i))::text
            """;
        var before = await PersistenceDatabase.ScalarAsync<string>(db.MigrationConnection, retained);
        Assert.Equal(new[] { "20261008000100_SiteCatalog" }, (await db.Runner.StatusAsync(default)).Pending); await db.Runner.ApplyAsync(default);
        Assert.Equal(before, await PersistenceDatabase.ScalarAsync<string>(db.ReaderConnection, retained));
        foreach (var table in new[] { "ins.site_identity", "ins.processes", "ins.devices", "ins.device_software_bindings", "rel.software" })
        { Assert.Equal(0, await PersistenceDatabase.ScalarAsync<long>(db.ReaderConnection, "SELECT count(*) FROM " + table)); Assert.True(await PersistenceDatabase.ScalarAsync<bool>(db.ReaderConnection,
            "SELECT NOT has_table_privilege(current_user,'" + table + "','INSERT') AND NOT has_table_privilege(current_user,'" + table + "','UPDATE')")); }
        var fk = await Assert.ThrowsAsync<PostgresException>(() => PersistenceDatabase.ExecuteAsync(db.WriterConnection,
            $"INSERT INTO ins.device_software_bindings VALUES ('{Guid.NewGuid()}','{Guid.NewGuid()}','{Guid.NewGuid()}',1,true,false)")); Assert.Equal("23503", fk.SqlState);
    }

    [Fact]
    public async Task GeneratedScriptWorksOnEmptyDatabaseAndMatchesApply()
    {
        var database = new PersistenceDatabase();
        try
        {
            await database.CreateAsync(migrate: false);
            var script = database.Runner.GenerateScript();
            await PersistenceDatabase.ExecuteAsync(database.MigrationConnection, script);
            await PersistenceDatabase.ExecuteAsync(database.MigrationConnection, script);
            Assert.Empty((await database.Runner.StatusAsync(default)).Pending);
            Assert.Equal(6, (await database.Runner.ApplyAsync(default)).Applied.Count);
            await PersistenceDatabase.ExecuteAsync(database.MigrationConnection, "CREATE TABLE iam.foundation_probe(id integer)");
            await PersistenceDatabase.ExecuteAsync(database.WriterConnection, "INSERT INTO iam.foundation_probe VALUES (7)");
            Assert.Equal(7, await PersistenceDatabase.ScalarAsync<int>(database.ReaderConnection, "SELECT id FROM iam.foundation_probe"));
        }
        finally { await database.DisposeAsync(); }
    }

    [Fact]
    public async Task HeldMigrationLockRejectsSecondMigratorAndIsReleasedOnConnectionClose()
    {
        var database = new PersistenceDatabase();
        try
        {
            await database.CreateAsync(migrate: false);
            await using (var connection = new NpgsqlConnection(database.MigrationConnection))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
                command.Parameters.AddWithValue("key", MigrationRunner.LockKey);
                Assert.Equal(true, await command.ExecuteScalarAsync());
                await Assert.ThrowsAsync<MigrationBusyException>(() => database.Runner.ApplyAsync(default));
                Assert.Equal(0, await PersistenceDatabase.ScalarAsync<long>(database.MigrationConnection, SchemaCount));
            }
            Assert.Equal(6, (await database.Runner.ApplyAsync(default)).Applied.Count);
        }
        finally { await database.DisposeAsync(); }
    }

    [Fact]
    public async Task PrivilegedRuntimeRolesAndWrongMigrationCredentialsAreRejectedSafely()
    {
        var database = new PersistenceDatabase();
        try
        {
            await database.CreateAsync(migrate: false);
            var privileged = new MigrationRunner(MigrationConfiguration.Create(database.MigrationConnection, "svm_bootstrap", database.ReaderRole));
            Assert.Equal(PersistenceFailure.ConfigurationInvalid,
                (await Assert.ThrowsAsync<PersistenceException>(() => privileged.ApplyAsync(default))).Failure);
            var scriptFailure = await Assert.ThrowsAsync<PostgresException>(() =>
                PersistenceDatabase.ExecuteAsync(database.MigrationConnection, privileged.GenerateScript()));
            Assert.Contains("role configuration invalid", scriptFailure.MessageText);
            Assert.Equal(0, await PersistenceDatabase.ScalarAsync<long>(database.MigrationConnection, SchemaCount));
            var settings = new NpgsqlConnectionStringBuilder(database.MigrationConnection) { Password = "invalid_" + Guid.NewGuid().ToString("N") };
            var wrong = new MigrationRunner(MigrationConfiguration.Create(settings.ConnectionString, database.WriterRole, database.ReaderRole));
            var error = await Assert.ThrowsAsync<PersistenceException>(() => wrong.ApplyAsync(default));
            Assert.Equal("28P01", error.SqlState);
            PersistenceDatabase.AssertRedacted(error.ToString(), settings.Password!);
            Assert.Null(error.InnerException);
        }
        finally { await database.DisposeAsync(); }
    }
}
