using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Framework;

internal sealed class PostgresUnitOfWork(SvmDbContext context) : IUnitOfWork
{
    private sealed class Operation(Guid id)
    {
        public Guid Id { get; } = id;
        public bool RollbackOnly;
        public bool Sealed;
    }
    private sealed class Frame(Operation operation) { public Operation Operation { get; } = operation; }
    private readonly object _gate = new();
    private readonly AsyncLocal<Frame?> _ambient = new();
    private Frame? _leaf;
    private Operation? _active;
    private bool _completed;
    public Guid? CurrentOperationId { get { lock (_gate) return _active?.Id; } }

    public async Task<T> ExecuteAsync<T>(Guid operationId, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation needs a stable identifier.", nameof(operationId));
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        Operation operation;
        Frame frame;
        var previous = _ambient.Value;
        bool nested;
        lock (_gate)
        {
            if (_completed) throw new PersistenceException(PersistenceFailure.ScopeCompleted, operationId);
            if (_active?.Sealed == true) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting, operationId);
            nested = _active is not null;
            if (nested && (_active!.Id != operationId || previous is null || !ReferenceEquals(previous, _leaf)))
            {
                _active!.RollbackOnly = true;
                throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting, operationId);
            }
            operation = _active ??= new Operation(operationId);
            frame = _leaf = new Frame(operation);
        }
        _ambient.Value = frame;
        if (nested)
        {
            try { return await action(cancellationToken); }
            catch { lock (_gate) operation.RollbackOnly = true; throw; }
            finally
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_leaf, frame)) operation.RollbackOnly = true;
                    else _leaf = previous;
                }
                _ambient.Value = previous;
            }
        }

        IDbContextTransaction? transaction = null;
        var commitAttempted = false;
        try
        {
            if (context.Database.CurrentTransaction is not null)
                throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting, operationId);
            await context.Database.OpenConnectionAsync(cancellationToken);
            await EnsureRuntimeRoleAsync((NpgsqlConnection)context.Database.GetDbConnection(), cancellationToken);
            transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var result = await action(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCanCommit(operation, frame);
            await context.SaveWithinUnitOfWorkAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCanCommit(operation, frame, seal: true);
            commitAttempted = true;
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception error)
        {
            if (transaction is not null)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await transaction.RollbackAsync(cleanup.Token);
                }
                catch (Exception rollbackError) when (rollbackError is DbException or InvalidOperationException or OperationCanceledException or IOException) { }
            }
            if (commitAttempted)
                throw new PersistenceException(PersistenceFailure.CommitOutcomeUnknown, operationId, (error as PostgresException)?.SqlState);
            if (error is DbException or DbUpdateException or IOException)
                throw new PersistenceException(PersistenceFailure.DependencyUnavailable, operationId,
                    (error as PostgresException ?? error.InnerException as PostgresException)?.SqlState);
            throw;
        }
        finally
        {
            lock (_gate) { _completed = true; _active = null; _leaf = null; }
            _ambient.Value = previous;
            context.ChangeTracker.Clear();
            // Cleanup cannot turn an acknowledged commit into a retryable failure.
            try { if (transaction is not null) await transaction.DisposeAsync(); }
            catch (Exception cleanupError) when (cleanupError is DbException or InvalidOperationException or OperationCanceledException or IOException) { }
            try { await context.Database.CloseConnectionAsync(); }
            catch (Exception cleanupError) when (cleanupError is DbException or InvalidOperationException or OperationCanceledException or IOException) { }
        }
    }

    private void EnsureCanCommit(Operation operation, Frame frame, bool seal = false)
    {
        lock (_gate)
        {
            if (operation.RollbackOnly || !ReferenceEquals(_leaf, frame))
                throw new PersistenceException(PersistenceFailure.OperationAborted, operation.Id);
            operation.Sealed = seal;
        }
    }

    private static async Task EnsureRuntimeRoleAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT NOT (r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls)
               AND NOT pg_has_role(current_user, d.datdba, 'MEMBER')
               AND NOT has_database_privilege(current_user, current_database(), 'CREATE')
               AND NOT EXISTS (SELECT 1 FROM pg_namespace n
                    WHERE n.nspname IN ('public','iam','rel','pkg','ins','tsk','aud','framework')
                    AND has_schema_privilege(current_user, n.oid, 'CREATE'))
            FROM pg_roles r JOIN pg_database d ON d.datname=current_database() WHERE r.rolname=current_user
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new PersistenceException(PersistenceFailure.ConfigurationInvalid);
    }
}
