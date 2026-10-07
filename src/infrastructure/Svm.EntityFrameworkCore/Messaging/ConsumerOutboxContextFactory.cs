using System.Data.Common;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using MassTransit.Middleware;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Messaging;

// Delegates the complete locking, Inbox, outgoing delivery and commit algorithm to the pinned component.
internal sealed class ConsumerOutboxContextFactory(SvmDbContext context,
    EntityFrameworkOutboxContextFactory<SvmDbContext> native, IIntegrationConsumptionRecovery recovery)
    : IOutboxContextFactory<SvmDbContext>
{
    public async Task Send<T>(ConsumeContext<T> consume, OutboxConsumeOptions options, IPipe<OutboxConsumeContext<T>> next) where T : class
    {
        context.ConsumerTransactions.Enter();
        try
        {
            await context.Database.OpenConnectionAsync(consume.CancellationToken);
            await PostgresUnitOfWork.EnsureRuntimeRoleAsync((NpgsqlConnection)context.Database.GetDbConnection(), consume.CancellationToken);
            await EnsureInboxPermissionsAsync((NpgsqlConnection)context.Database.GetDbConnection(), consume.CancellationToken);
            await native.Send(consume, options, next);
        }
        catch (Exception error)
        {
            if (context.ConsumerTransactions.CommitAttempted && !context.ConsumerTransactions.CommitConfirmed)
            {
                // Verification may recover a result, but never re-enters the handler or acknowledges retained events.
                using var verification = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { if (consume.Message is IIntegrationEvent message) await recovery.FindAsync(message, verification.Token); }
                catch (Exception verifyError) when (verifyError is PersistenceException or IntegrationConsumptionException or OperationCanceledException or DbException or IOException) { }
                throw new PersistenceException(PersistenceFailure.CommitOutcomeUnknown, context.ConsumerTransactions.OperationId);
            }
            var postgres = error as PostgresException ?? error.InnerException as PostgresException;
            if (!context.ConsumerTransactions.CommitAttempted && context.ConsumerTransactions.OperationId is null &&
                postgres is { SqlState: "23505", ConstraintName: "AK_InboxState_MessageId_ConsumerId" })
                throw new IntegrationConsumptionException(ConsumptionFailure.TransientClaimConflict);
            if (error is DbException or DbUpdateException or IOException)
                throw new PersistenceException(PersistenceFailure.DependencyUnavailable, context.ConsumerTransactions.OperationId,
                    postgres?.SqlState);
            throw;
        }
        finally { context.ConsumerTransactions.Exit(); }
    }
    public void Probe(ProbeContext context) => native.Probe(context);

    private static async Task EnsureInboxPermissionsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT has_table_privilege(current_user,'framework."InboxState"','SELECT')
               AND has_table_privilege(current_user,'framework."InboxState"','INSERT')
               AND has_table_privilege(current_user,'framework."InboxState"','UPDATE')
               AND has_table_privilege(current_user,'framework."InboxState"','DELETE')
               AND has_sequence_privilege(current_user,'framework."InboxState_Id_seq"','USAGE')
               AND has_sequence_privilege(current_user,'framework."InboxState_Id_seq"','SELECT')
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new PersistenceException(PersistenceFailure.ConfigurationInvalid);
    }
}
