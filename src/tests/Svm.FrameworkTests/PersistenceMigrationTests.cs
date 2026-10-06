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
    public async Task EmptyDatabaseStatusAndScriptDoNotMutateAndApplyCanBeRepeated()
    {
        var database = new PersistenceDatabase();
        try
        {
            await database.CreateAsync(migrate: false);
            var before = await database.Runner.StatusAsync(default);
            Assert.Empty(before.Applied);
            Assert.Equal(new[] { "20260930000100_InitialSchemas", "20261001000100_PersonnelSessions" }, before.Pending);
            var script = database.Runner.GenerateScript();
            Assert.Contains("pg_try_advisory_lock", script);
            Assert.Contains("SVM migration role configuration invalid", script);
            PersistenceDatabase.AssertRedacted(script, new NpgsqlConnectionStringBuilder(database.MigrationConnection).Password!);
            Assert.Equal(0, await PersistenceDatabase.ScalarAsync<long>(database.MigrationConnection, SchemaCount));
            var first = await database.Runner.ApplyAsync(default);
            var second = await database.Runner.ApplyAsync(default);
            Assert.Empty(first.Pending);
            Assert.Equal(first.Applied, second.Applied);
            Assert.Equal(2, second.Applied.Count);
            Assert.Equal(7, await PersistenceDatabase.ScalarAsync<long>(database.MigrationConnection, SchemaCount));
            Assert.Equal(10, await PersistenceDatabase.ScalarAsync<long>(database.MigrationConnection,
                "SELECT count(*) FROM pg_tables WHERE schemaname IN ('iam','rel','pkg','ins','tsk','aud','framework')"));
            Assert.Equal(2, await PersistenceDatabase.ScalarAsync<long>(database.MigrationConnection,
                "SELECT count(*) FROM framework.\"__EFMigrationsHistory\""));
        }
        finally { await database.DisposeAsync(); }
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
            Assert.Equal(2, (await database.Runner.ApplyAsync(default)).Applied.Count);
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
            Assert.Equal(2, (await database.Runner.ApplyAsync(default)).Applied.Count);
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
