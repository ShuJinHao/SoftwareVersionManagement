using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Svm.Dapper;
using Svm.EntityFrameworkCore;
using Svm.EntityFrameworkCore.Configuration;
using Svm.EntityFrameworkCore.Framework;
using Svm.EntityFrameworkCore.Migrations;
using Svm.Services.Contracts.Framework;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class PersistenceTests(PersistenceDatabase database) : IClassFixture<PersistenceDatabase>
{
    [Fact]
    public async Task CommitUsesReadCommittedAndSameOperationJoinsOneTransaction()
    {
        await using var provider = database.CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var context = scope.ServiceProvider.GetRequiredService<SvmDbContext>();
        var operation = Guid.NewGuid();
        var id = Guid.NewGuid();
        var result = await unit.ExecuteAsync(operation, async token =>
        {
            Assert.Equal(operation, unit.CurrentOperationId);
            Assert.Equal(System.Data.IsolationLevel.ReadCommitted, context.Database.CurrentTransaction!.GetDbTransaction().IsolationLevel);
            var transaction = context.Database.CurrentTransaction;
            await PersistenceDatabase.InsertAsync(context, id, 42, token);
            return await unit.ExecuteAsync(operation, async nestedToken =>
            {
                Assert.Same(transaction, context.Database.CurrentTransaction);
                await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE iam.foundation_probe SET value={43} WHERE id={id}", nestedToken);
                return 43;
            }, token);
        }, default);
        Assert.Equal(43, result);
        Assert.Null(unit.CurrentOperationId);
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(1, await database.CountAsync(id));
        var read = scope.ServiceProvider.GetRequiredService<ReadQuerySession>();
        Assert.Equal(43, Assert.Single(await read.QueryAsync<int>("SELECT value FROM iam.foundation_probe WHERE id=@id", new { id }, default)));
        Assert.Equal(PersistenceFailure.ScopeCompleted, (await Assert.ThrowsAsync<PersistenceException>(() =>
            unit.ExecuteAsync(Guid.NewGuid(), _ => Task.FromResult(0), default))).Failure);
    }

    [Fact]
    public async Task ExceptionRollsBackAndCaughtNestedFailureStillAbortsOuterOperation()
    {
        await using var provider = database.CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var context = scope.ServiceProvider.GetRequiredService<SvmDbContext>();
        var id = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<PersistenceException>(() => unit.ExecuteAsync(operation, async token =>
        {
            await PersistenceDatabase.InsertAsync(context, id, 1, token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => unit.ExecuteAsync<int>(operation,
                _ => throw new InvalidOperationException("Controlled domain failure."), token));
            return 1;
        }, default));
        Assert.Equal(PersistenceFailure.OperationAborted, error.Failure);
        Assert.Equal(0, await database.CountAsync(id));
    }

    [Fact]
    public async Task CancellationAfterWriteRollsBackWithoutReplaying()
    {
        await using var provider = database.CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        using var cancellation = new CancellationTokenSource();
        var id = Guid.NewGuid();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .ExecuteAsync(Guid.NewGuid(), async token =>
            {
                calls++;
                await PersistenceDatabase.InsertAsync(scope.ServiceProvider.GetRequiredService<SvmDbContext>(), id, 1, token);
                cancellation.Cancel();
                return 1;
            }, cancellation.Token));
        Assert.Equal(1, calls);
        Assert.Equal(0, await database.CountAsync(id));
    }

    [Fact]
    public async Task DifferentOperationCannotCommitInsideExistingTransaction()
    {
        await using var provider = database.CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var id = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<PersistenceException>(() => unit.ExecuteAsync(Guid.NewGuid(), async token =>
        {
            await PersistenceDatabase.InsertAsync(scope.ServiceProvider.GetRequiredService<SvmDbContext>(), id, 1, token);
            var nested = await Assert.ThrowsAsync<PersistenceException>(() => unit.ExecuteAsync(Guid.NewGuid(), _ => Task.FromResult(0), token));
            Assert.Equal(PersistenceFailure.InvalidTransactionNesting, nested.Failure);
            return 0;
        }, default));
        Assert.Equal(PersistenceFailure.OperationAborted, error.Failure);
        Assert.Equal(0, await database.CountAsync(id));
    }

    [Fact]
    public async Task ParallelUseOfSameScopeIsRejectedAndOriginalWriteRollsBack()
    {
        await using var provider = database.CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var first = unit.ExecuteAsync(operation, async token =>
        {
            await PersistenceDatabase.InsertAsync(scope.ServiceProvider.GetRequiredService<SvmDbContext>(), id, 1, token);
            started.SetResult();
            await finish.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            return 0;
        }, default);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var error = await Assert.ThrowsAsync<PersistenceException>(() => unit.ExecuteAsync(operation, _ => Task.FromResult(0), default));
            Assert.Equal(PersistenceFailure.InvalidTransactionNesting, error.Failure);
        }
        finally { finish.TrySetResult(); }
        Assert.Equal(PersistenceFailure.OperationAborted, (await Assert.ThrowsAsync<PersistenceException>(() => first)).Failure);
        Assert.Equal(0, await database.CountAsync(id));
    }

    [Fact]
    public async Task SeparateScopesHaveIndependentContextsTransactionsAndDisposal()
    {
        await using var provider = database.CreateProvider();
        SvmDbContext disposed;
        await using (var first = provider.CreateAsyncScope())
        await using (var second = provider.CreateAsyncScope())
        {
            var firstContext = first.ServiceProvider.GetRequiredService<SvmDbContext>();
            disposed = firstContext;
            Assert.Same(firstContext, first.ServiceProvider.GetRequiredService<SvmDbContext>());
            Assert.NotEqual(firstContext.ContextId, second.ServiceProvider.GetRequiredService<SvmDbContext>().ContextId);
            var id = Guid.NewGuid();
            var otherId = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(() => first.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .ExecuteAsync<int>(Guid.NewGuid(), async token =>
                {
                    await PersistenceDatabase.InsertAsync(firstContext, id, 1, token);
                    Assert.Equal(0, await database.CountAsync(id));
                    await second.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteAsync(Guid.NewGuid(), async secondToken =>
                    {
                        await PersistenceDatabase.InsertAsync(second.ServiceProvider.GetRequiredService<SvmDbContext>(), otherId, 2, secondToken);
                        return 2;
                    }, token);
                    throw new InvalidOperationException("Roll back only the first scope.");
                }, default));
            Assert.Equal(0, await database.CountAsync(id));
            Assert.Equal(1, await database.CountAsync(otherId));
        }
        Assert.Throws<ObjectDisposedException>(() => disposed.Model);
    }

    [Fact]
    public async Task ConnectionLossBeforeCommitDoesNotReplayAction()
    {
        await using var provider = database.CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SvmDbContext>();
        var id = Guid.NewGuid();
        var calls = 0;
        var error = await Assert.ThrowsAsync<PersistenceException>(() => scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .ExecuteAsync(Guid.NewGuid(), async token =>
            {
                calls++;
                await PersistenceDatabase.InsertAsync(context, id, 1, token);
                Assert.True(await database.TerminateOwnConnectionAsync(((NpgsqlConnection)context.Database.GetDbConnection()).ProcessID));
                await context.Database.ExecuteSqlRawAsync("SELECT 1", token);
                return 0;
            }, default));
        Assert.Equal(PersistenceFailure.DependencyUnavailable, error.Failure);
        Assert.Equal(1, calls);
        Assert.Equal(0, await database.CountAsync(id));
    }

    [Fact]
    public async Task LostCommitAcknowledgementReturnsUnknownAndNeverReplaysCommittedWork()
    {
        var fault = new LostCommitAcknowledgement();
        await using var provider = database.CreateProvider(fault);
        await using var scope = provider.CreateAsyncScope();
        var id = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var calls = 0;
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var error = await Assert.ThrowsAsync<PersistenceException>(() => unit.ExecuteAsync(operation, async token =>
        {
            calls++;
            await PersistenceDatabase.InsertAsync(scope.ServiceProvider.GetRequiredService<SvmDbContext>(), id, 1, token);
            return 0;
        }, default));
        Assert.Equal(PersistenceFailure.CommitOutcomeUnknown, error.Failure);
        Assert.Equal(operation, error.OperationId);
        Assert.Equal(1, calls);
        Assert.Equal(1, fault.Count);
        Assert.Equal(1, await database.CountAsync(id));
        Assert.Null(error.InnerException);
        Assert.Equal(PersistenceFailure.ScopeCompleted, (await Assert.ThrowsAsync<PersistenceException>(() =>
            unit.ExecuteAsync(operation, _ => Task.FromResult(0), default))).Failure);
    }

    [Theory]
    [InlineData("CREATE TABLE iam.forbidden_probe(id integer)")]
    [InlineData("CREATE SCHEMA forbidden_probe")]
    [InlineData("CREATE TEMP TABLE forbidden_probe(id integer)")]
    [InlineData("DELETE FROM framework.\"__EFMigrationsHistory\"")]
    public async Task RuntimeAccountCannotChangeStructureOrMigrationHistory(string sql)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => PersistenceDatabase.ExecuteAsync(database.WriterConnection, sql));
        Assert.Equal("42501", error.SqlState);
    }

    [Fact]
    public async Task ReaderIsBlockedByDatabaseGrantsEvenWhenDefaultReadOnlyIsTurnedOff()
    {
        await using var connection = new NpgsqlConnection(database.ReaderConnection);
        await connection.OpenAsync();
        await using var setting = new NpgsqlCommand("SET default_transaction_read_only=off", connection);
        await setting.ExecuteNonQueryAsync();
        await using var write = new NpgsqlCommand("INSERT INTO iam.foundation_probe VALUES (gen_random_uuid(),1)", connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync());
        Assert.Equal("42501", error.SqlState);
        await using var provider = database.CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var read = scope.ServiceProvider.GetRequiredService<ReadQuerySession>();
        Assert.Equal("on", Assert.Single(await read.QueryAsync<string>("SHOW transaction_read_only", null, default)));
        var readError = await Assert.ThrowsAsync<PersistenceException>(() => read.QueryAsync<int>(
            "INSERT INTO iam.foundation_probe VALUES (gen_random_uuid(),1) RETURNING value", null, default));
        Assert.Equal("25006", readError.SqlState);
    }

    [Fact]
    public async Task ReadCancellationClosesConnectionAndAllowsTheNextQuery()
    {
        await using var provider = database.CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var read = scope.ServiceProvider.GetRequiredService<ReadQuerySession>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.QueryAsync<int>("SELECT 1 FROM pg_sleep(30)", null, cancellation.Token));
        Assert.Equal(7, Assert.Single(await read.QueryAsync<int>("SELECT 7", null, default)));
    }

    [Fact]
    public async Task MisassignedRolesAreRejectedBeforeUseCaseAndQueries()
    {
        await using var provider = database.CreateProvider(writer: database.MigrationConnection, reader: database.WriterConnection);
        await using var scope = provider.CreateAsyncScope();
        var calls = 0;
        var write = await Assert.ThrowsAsync<PersistenceException>(() => scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .ExecuteAsync(Guid.NewGuid(), _ => { calls++; return Task.FromResult(0); }, default));
        Assert.Equal(PersistenceFailure.ConfigurationInvalid, write.Failure);
        Assert.Equal(0, calls);
        var read = await Assert.ThrowsAsync<PersistenceException>(() => scope.ServiceProvider.GetRequiredService<ReadQuerySession>()
            .QueryAsync<int>("SELECT 1", null, default));
        Assert.Equal(PersistenceFailure.ConfigurationInvalid, read.Failure);
    }

    [Fact]
    public async Task WrongPasswordIsRejectedWithoutReturningSecrets()
    {
        var settings = new NpgsqlConnectionStringBuilder(database.WriterConnection) { Password = "invalid_" + Guid.NewGuid().ToString("N") };
        await using var provider = database.CreateProvider(writer: settings.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var error = await Assert.ThrowsAsync<PersistenceException>(() => scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .ExecuteAsync(Guid.NewGuid(), _ => Task.FromResult(0), default));
        Assert.Equal(PersistenceFailure.DependencyUnavailable, error.Failure);
        Assert.Equal("28P01", error.SqlState);
        PersistenceDatabase.AssertRedacted(error.ToString(), settings.Password!);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("Include Error Detail=true")]
    [InlineData("Log Parameters=true")]
    [InlineData("No Reset On Close=true")]
    [InlineData("Multiplexing=true")]
    [InlineData("Command Timeout=0")]
    [InlineData("Username=")]
    public void UnsafeConfigurationIsRejectedWithoutEchoingInput(string overrideOption)
    {
        var input = database.WriterConnection + ";" + overrideOption;
        var writer = Assert.Throws<PersistenceException>(() => PostgresConnectionOptions.Parse(input));
        var reader = Assert.Throws<PersistenceException>(() => new ServiceCollection().AddSvmReadPersistence(input));
        var password = new NpgsqlConnectionStringBuilder(database.WriterConnection).Password!;
        Assert.Equal(PersistenceFailure.ConfigurationInvalid, writer.Failure);
        Assert.Equal(PersistenceFailure.ConfigurationInvalid, reader.Failure);
        PersistenceDatabase.AssertRedacted(writer.ToString(), password);
        PersistenceDatabase.AssertRedacted(reader.ToString(), password);
        Assert.Null(writer.InnerException);
        Assert.Null(reader.InnerException);
    }

    [Fact]
    public async Task ContextIsInternalCannotSaveDirectlyAndDuplicateRegistrationIsRejected()
    {
        Assert.False(typeof(SvmDbContext).IsPublic);
        Assert.False(typeof(ReadQuerySession).IsPublic);
        await using var provider = database.CreateProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IUnitOfWork>());
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SvmDbContext>();
        var operationResults = context.Model.GetEntityTypes().Where(type => type.GetTableName() == "operation_results").ToArray();
        Assert.Equal(new[] { "aud", "iam", "ins", "pkg", "rel", "tsk" }, operationResults.Select(type => type.GetSchema()).Order());
        Assert.All(context.Model.GetEntityTypes().Except(operationResults), type => Assert.Contains(type.GetSchema(), new[] { "iam", "aud" }));
        Assert.DoesNotContain(context.Model.GetEntityTypes(), type => type.GetTableName() == "foundation_probe");
        Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        var services = new ServiceCollection().AddSvmPostgres(database.WriterConnection).AddSvmReadPersistence(database.ReaderConnection);
        Assert.Throws<InvalidOperationException>(() => services.AddSvmPostgres(database.WriterConnection));
        Assert.Throws<InvalidOperationException>(() => services.AddSvmReadPersistence(database.ReaderConnection));
    }

    private sealed class LostCommitAcknowledgement : DbTransactionInterceptor
    {
        internal int Count;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Count++;
            // Real PostgreSQL has committed. Simulate loss of the application-visible acknowledgement.
            throw new IOException("Controlled loss of commit acknowledgement.");
        }
    }
}
