namespace Svm.Services.Contracts.Framework;

public interface IUnitOfWork
{
    Guid? CurrentOperationId { get; }
    /// <summary>
    /// Runs one atomic operation per scope. Awaited nested calls may join the same operation;
    /// parallel use and an independent nested commit are rejected. Failures are never replayed automatically.
    /// </summary>
    Task<T> ExecuteAsync<T>(Guid operationId, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken);
}

public enum PersistenceFailure
{
    ConfigurationInvalid = 1, DependencyUnavailable, OperationAborted, CommitOutcomeUnknown,
    InvalidTransactionNesting, ScopeCompleted
}

/// <summary>Internal storage failure; HTTP mapping remains the existing API error contract.</summary>
public sealed class PersistenceException : Exception
{
    public PersistenceException(PersistenceFailure failure, Guid? operationId = null, string? sqlState = null)
        : base($"Persistence operation failed: {failure}.")
    {
        Failure = failure;
        OperationId = operationId;
        SqlState = sqlState is { Length: 5 } && sqlState.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)) ? sqlState : null;
    }
    public PersistenceFailure Failure { get; }
    public Guid? OperationId { get; }
    public string? SqlState { get; }
}
