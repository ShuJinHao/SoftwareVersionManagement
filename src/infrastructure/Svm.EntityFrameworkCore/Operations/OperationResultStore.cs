using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Operations;

internal sealed class OperationResultStore(SvmDbContext context, IUnitOfWork unitOfWork, IOperationContext? operationContext = null)
    : IOperationResultStore
{
    private OperationIdentity Identity => operationContext?.Current ?? throw new PersistenceException(PersistenceFailure.ConfigurationInvalid);
    private static string Schema(ModuleOwner owner) => owner switch
    {
        ModuleOwner.Identity => "iam", ModuleOwner.Releases => "rel", ModuleOwner.Packages => "pkg",
        ModuleOwner.Instances => "ins", ModuleOwner.Tasks => "tsk", ModuleOwner.Audit => "aud",
        _ => throw new PersistenceException(PersistenceFailure.ConfigurationInvalid)
    };

    public async Task<StoredOperationResult?> FindAsync(CancellationToken cancellationToken)
    {
        var identity = Identity;
        try
        {
            var row = await context.Set<Dictionary<string, object>>("Svm.OperationResult." + Schema(identity.Owner))
                .AsNoTracking().SingleOrDefaultAsync(r => EF.Property<short>(r, "ActorKind") == (short)identity.ActorKind &&
                    EF.Property<Guid>(r, "SubjectId") == identity.SubjectId && EF.Property<string>(r, "Operation") == identity.Operation &&
                    EF.Property<Guid>(r, "IdempotencyKey") == identity.Key, cancellationToken);
            if (row is null) return null;
            if (row["Status"] is not short status || row["CompletedAt"] is null)
                throw new PersistenceException(PersistenceFailure.DependencyUnavailable, unitOfWork.CurrentOperationId);
            return new((string)row["RequestDigest"], new((Guid)row["OperationId"], (OperationStatus)status,
                row["ResourceId"] as Guid?, row["WorkId"] as Guid?));
        }
        catch (Exception error) when (error is DbException or DbUpdateException or IOException) { throw Redact(error); }
    }

    public async Task<bool> TryAcquireAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var identity = Identity;
        RequireTransaction(operationId);
        if (context.PendingOperationResult is not null) throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting, operationId);
        try
        {
            // The schema is selected exclusively from fixed owner metadata; all values are parameters.
            var sql = $"""
                INSERT INTO {Schema(identity.Owner)}.operation_results
                    ("ActorKind","SubjectId","Operation","IdempotencyKey","RequestDigest","OperationId")
                VALUES (@actor,@subject,@operation,@key,@digest,@id)
                ON CONFLICT ("ActorKind","SubjectId","Operation","IdempotencyKey") DO NOTHING
                """;
            var count = await context.Database.ExecuteSqlRawAsync(sql, Parameters(identity, operationId), cancellationToken);
            if (count == 1) context.PendingOperationResult = operationId;
            return count == 1;
        }
        catch (Exception error) when (error is DbException or DbUpdateException or IOException) { throw Redact(error); }
    }

    public async Task CompleteAsync(OperationResultReference result, CancellationToken cancellationToken)
    {
        var identity = Identity;
        RequireTransaction(result.OperationId);
        if (context.PendingOperationResult != result.OperationId)
            throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting, result.OperationId);
        try
        {
            var sql = $"""
                UPDATE {Schema(identity.Owner)}.operation_results
                SET "Status"=@status,"ResourceId"=@resource,"WorkId"=@work,"CompletedAt"=clock_timestamp()
                WHERE "ActorKind"=@actor AND "SubjectId"=@subject AND "Operation"=@operation
                  AND "IdempotencyKey"=@key AND "RequestDigest"=@digest AND "OperationId"=@id AND "Status" IS NULL
                """;
            var parameters = Parameters(identity, result.OperationId).Concat(new NpgsqlParameter[]
            {
                new("status", (short)result.Status), new("resource", NpgsqlDbType.Uuid) { Value = (object?)result.ResourceId ?? DBNull.Value },
                new("work", NpgsqlDbType.Uuid) { Value = (object?)result.WorkId ?? DBNull.Value }
            });
            if (await context.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken) != 1)
                throw new PersistenceException(PersistenceFailure.OperationAborted, result.OperationId);
            context.PendingOperationResult = null;
        }
        catch (Exception error) when (error is DbException or DbUpdateException or IOException) { throw Redact(error); }
    }

    private void RequireTransaction(Guid operationId)
    {
        if (operationId == Guid.Empty || context.Database.CurrentTransaction is null || unitOfWork.CurrentOperationId != operationId)
            throw new PersistenceException(PersistenceFailure.InvalidTransactionNesting, operationId);
    }
    private static NpgsqlParameter[] Parameters(OperationIdentity identity, Guid operationId) =>
        [new("actor", (short)identity.ActorKind), new("subject", identity.SubjectId), new("operation", identity.Operation),
         new("key", identity.Key), new("digest", identity.RequestDigest), new("id", operationId)];
    private PersistenceException Redact(Exception error) => new(PersistenceFailure.DependencyUnavailable,
        unitOfWork.CurrentOperationId, (error as PostgresException ?? error.InnerException as PostgresException)?.SqlState);
}
