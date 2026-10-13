using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Migrations;

public sealed record MigrationStatus(IReadOnlyList<string> Applied, IReadOnlyList<string> Pending);
public sealed class MigrationBusyException() : Exception("Another SVM migration owns the database migration lock.");

public sealed class MigrationRunner(MigrationConfiguration configuration)
{
    // A session lock on the migration connection, scoped by PostgreSQL to the current database.
    internal const long LockKey = 0x53564D4D494752;
    internal static readonly string[] Schemas = ["iam", "rel", "pkg", "ins", "tsk", "aud", "framework"];
    private const string RoleCheckSql = """
        SELECT d.datdba = me.oid AND NOT (me.rolsuper OR me.rolcreatedb OR me.rolcreaterole OR me.rolreplication OR me.rolbypassrls)
          AND w.rolcanlogin AND q.rolcanlogin
          AND NOT (w.rolsuper OR w.rolcreatedb OR w.rolcreaterole OR w.rolreplication OR w.rolbypassrls)
          AND NOT (q.rolsuper OR q.rolcreatedb OR q.rolcreaterole OR q.rolreplication OR q.rolbypassrls)
          AND NOT pg_has_role(w.oid, me.oid, 'MEMBER') AND NOT pg_has_role(q.oid, me.oid, 'MEMBER')
          AND NOT pg_has_role(q.oid, w.oid, 'MEMBER')
        FROM pg_database d JOIN pg_roles me ON me.rolname=current_user
          JOIN pg_roles w ON w.rolname=@writer JOIN pg_roles q ON q.rolname=@reader
        WHERE d.datname=current_database()
        """;

    private NpgsqlConnection CreateConnection()
    {
        var builder = new NpgsqlConnectionStringBuilder(configuration.Connection.ConnectionString) { Pooling = false };
        return new NpgsqlConnection(builder.ConnectionString);
    }
    private static SvmDbContext CreateContext(NpgsqlConnection connection) => new(new DbContextOptionsBuilder<SvmDbContext>()
        .UseNpgsql(connection, postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "framework")).Options);

    public string GenerateScript()
    {
        using var connection = CreateConnection();
        using var context = CreateContext(connection);
        var script = context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        var roles = RoleCheckSql.Replace("@writer", $"'{configuration.WriterRole}'").Replace("@reader", $"'{configuration.ReaderRole}'");
        return "-- Execute on one dedicated connection, with stop-on-error enabled; close the connection on every exit.\n" +
            $"DO $svm$ BEGIN IF NOT COALESCE(({roles}),false) THEN RAISE EXCEPTION 'SVM migration role configuration invalid'; END IF; END $svm$;\n" +
            $"DO $svm$ BEGIN IF NOT pg_try_advisory_lock({LockKey}) THEN RAISE EXCEPTION 'SVM migration busy'; END IF; END $svm$;\n" +
            script + "\nBEGIN;\n" + PermissionSql() + $"\nCOMMIT;\nSELECT pg_advisory_unlock({LockKey});\n";
    }

    public async Task<MigrationStatus> StatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await EnsureRolesAsync(connection, cancellationToken);
            await using var context = CreateContext(connection);
            return new((await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray(),
                (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray());
        }
        catch (Exception error) when (error is DbException or IOException)
        { throw new PersistenceException(PersistenceFailure.DependencyUnavailable, sqlState: (error as PostgresException)?.SqlState); }
    }

    public async Task<MigrationStatus> ApplyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await EnsureRolesAsync(connection, cancellationToken);
            await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            acquire.Parameters.AddWithValue("key", LockKey);
            if (await acquire.ExecuteScalarAsync(cancellationToken) is not true) throw new MigrationBusyException();
            // The non-pooled session releases the lock on every exit, including cancellation or broken connections.
            await using var context = CreateContext(connection);
            await context.Database.MigrateAsync(cancellationToken);
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                await using var grant = new NpgsqlCommand(PermissionSql(), connection, transaction);
                await grant.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            return new((await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray(),
                (await context.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray());
        }
        catch (Exception error) when (error is DbException or IOException)
        { throw new PersistenceException(PersistenceFailure.DependencyUnavailable, sqlState: (error as PostgresException)?.SqlState); }
    }

    private async Task EnsureRolesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RoleCheckSql, connection);
        command.Parameters.AddWithValue("writer", configuration.WriterRole);
        command.Parameters.AddWithValue("reader", configuration.ReaderRole);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new PersistenceException(PersistenceFailure.ConfigurationInvalid);
    }

    private string PermissionSql()
    {
        // Role names are validated identifier tokens, never arbitrary SQL fragments or credentials.
        var writer = $"\"{configuration.WriterRole}\"";
        var reader = $"\"{configuration.ReaderRole}\"";
        var sql = new System.Text.StringBuilder();
        sql.AppendLine($"REVOKE CREATE ON SCHEMA public FROM PUBLIC, {writer}, {reader};");
        sql.AppendLine($"REVOKE ALL ON SCHEMA public FROM {writer}, {reader};");
        foreach (var schema in Schemas)
        {
            sql.AppendLine($"REVOKE CREATE ON SCHEMA {schema} FROM PUBLIC, {writer}, {reader};");
            sql.AppendLine($"GRANT USAGE ON SCHEMA {schema} TO {writer}, {reader};");
            sql.AppendLine($"GRANT SELECT ON ALL TABLES IN SCHEMA {schema} TO {reader};");
            sql.AppendLine($"ALTER DEFAULT PRIVILEGES IN SCHEMA {schema} GRANT SELECT ON TABLES TO {reader};");
            if (schema == "framework") continue; // Framework writers are granted by each future technical-table migration.
            var writes = schema == "aud" ? "SELECT, INSERT" : "SELECT, INSERT, UPDATE, DELETE";
            sql.AppendLine($"GRANT {writes} ON ALL TABLES IN SCHEMA {schema} TO {writer};");
            sql.AppendLine($"ALTER DEFAULT PRIVILEGES IN SCHEMA {schema} GRANT {writes} ON TABLES TO {writer};");
            sql.AppendLine($"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {schema} TO {writer};");
            sql.AppendLine($"ALTER DEFAULT PRIVILEGES IN SCHEMA {schema} GRANT USAGE, SELECT ON SEQUENCES TO {writer};");
        }
        sql.AppendLine($"REVOKE ALL ON TABLE framework.\"__EFMigrationsHistory\" FROM PUBLIC, {writer};");
        sql.AppendLine($"GRANT SELECT, INSERT ON TABLE framework.data_protection_keys TO {writer};");
        sql.AppendLine($"GRANT USAGE, SELECT ON SEQUENCE framework.\"data_protection_keys_Id_seq\" TO {writer};");
        sql.AppendLine($"GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE framework.\"OutboxMessage\", framework.\"OutboxState\" TO {writer};");
        sql.AppendLine($"GRANT USAGE, SELECT ON SEQUENCE framework.\"OutboxMessage_SequenceNumber_seq\" TO {writer};");
        if (configuration.EnableInboxWrites)
        {
            sql.AppendLine($"GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE framework.\"InboxState\" TO {writer};");
            sql.AppendLine($"GRANT USAGE, SELECT ON SEQUENCE framework.\"InboxState_Id_seq\" TO {writer};");
        }
        else
        {
            sql.AppendLine($"REVOKE ALL ON TABLE framework.\"InboxState\" FROM {writer};");
            sql.AppendLine($"REVOKE ALL ON SEQUENCE framework.\"InboxState_Id_seq\" FROM {writer};");
        }
        foreach (var schema in new[] { "iam", "rel", "pkg", "ins", "tsk", "aud" })
        {
            // A completed operation key is retained; writers can only finalize its result columns.
            sql.AppendLine($"REVOKE UPDATE, DELETE ON TABLE {schema}.operation_results FROM {writer};");
            sql.AppendLine($"GRANT UPDATE (\"Status\",\"ResourceId\",\"WorkId\",\"CompletedAt\") ON TABLE {schema}.operation_results TO {writer};");
        }
        foreach (var (table, columns) in new[] {
            ("instance_credentials", "\"Id\",\"SubjectId\",\"ExpiresAt\",\"RevokedAt\",\"Revision\""),
            ("enrollment_grants", "\"Id\",\"SoftwareId\",\"DeviceIds\",\"ExpiresAt\",\"MaxInstances\",\"UsedCount\",\"RevokedAt\",\"Revision\""),
            ("recovery_grants", "\"Id\",\"SoftwareId\",\"InstanceId\",\"ExpiresAt\",\"UsedKey\",\"RevokedAt\",\"Revision\"") })
        {
            sql.AppendLine($"REVOKE SELECT ON TABLE iam.{table} FROM {reader};");
            sql.AppendLine($"GRANT SELECT ({columns}) ON TABLE iam.{table} TO {reader};");
        }
        sql.AppendLine($"REVOKE ALL ON TABLE iam.registrations FROM {reader};");
        sql.AppendLine($"REVOKE DELETE ON TABLE iam.instance_subjects,iam.instance_credentials,iam.enrollment_grants,iam.recovery_grants,iam.registrations,ins.instances,ins.instance_snapshots,ins.installation_evidence,ins.report_stream_receipts FROM {writer};");
        sql.AppendLine($"REVOKE UPDATE ON TABLE iam.registrations,ins.installation_evidence,ins.report_stream_receipts FROM {writer};");
        sql.AppendLine($"REVOKE DELETE ON TABLE rel.releases,pkg.packages,pkg.works,pkg.replicas,pkg.download_sessions,pkg.dispatches,pkg.receive_attempts FROM {writer};");
        sql.AppendLine($"REVOKE UPDATE ON TABLE pkg.dispatches,pkg.receive_attempts FROM {writer};");
        sql.AppendLine($"REVOKE DELETE ON TABLE rel.integration_materials,tsk.target_selections,tsk.target_members,tsk.selection_chunks,tsk.deployments,tsk.admission_items,tsk.batches,tsk.tasks,tsk.attempts,tsk.receipts,tsk.works,tsk.dispatches,tsk.control_items FROM {writer};");
        sql.AppendLine($"REVOKE UPDATE ON TABLE rel.integration_materials,tsk.target_members,tsk.selection_chunks,tsk.admission_items,tsk.receipts,tsk.dispatches,tsk.control_items FROM {writer};");
        return sql.ToString();
    }
}
