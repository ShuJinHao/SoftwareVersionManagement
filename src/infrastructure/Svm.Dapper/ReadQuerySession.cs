using System.Data;
using System.Data.Common;
using Dapper;
using Npgsql;
using Svm.Services.Contracts.Framework;

namespace Svm.Dapper;

/// <summary>Infrastructure-only executor. Module query implementations own SQL and return their DTOs.</summary>
internal sealed class ReadQuerySession(ReadDataSource source)
{
    internal async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition("SET TRANSACTION READ ONLY", transaction: transaction, cancellationToken: cancellationToken));
            const string roleSql = """
                SELECT NOT (r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls)
                 AND NOT pg_has_role(current_user, d.datdba, 'MEMBER')
                 AND NOT has_database_privilege(current_user,current_database(),'CREATE')
                 AND NOT EXISTS (SELECT 1 FROM pg_namespace n
                       WHERE n.nspname IN ('public','iam','rel','pkg','ins','tsk','aud','framework')
                       AND has_schema_privilege(current_user,n.oid,'CREATE'))
                 AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                       WHERE n.nspname IN ('iam','rel','pkg','ins','tsk','aud','framework') AND c.relkind IN ('r','p','v','m','f')
                       AND has_table_privilege(current_user,c.oid,'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER'))
                FROM pg_roles r JOIN pg_database d ON d.datname=current_database() WHERE r.rolname=current_user
                """;
            if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(roleSql, transaction: transaction, cancellationToken: cancellationToken)))
                throw new PersistenceException(PersistenceFailure.ConfigurationInvalid);
            var result = await connection.QueryAsync<T>(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return result.AsList().AsReadOnly();
        }
        catch (Exception error) when (error is DbException or IOException)
        { throw new PersistenceException(PersistenceFailure.DependencyUnavailable, sqlState: (error as PostgresException)?.SqlState); }
    }
}
