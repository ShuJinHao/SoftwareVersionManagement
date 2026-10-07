using MediatR;

namespace Svm.Services.Contracts.Framework;

public interface ICommand<T> : IRequest<OperationResult<T>>;
public interface IQuery<T> : IRequest<T>;

public enum OperationStatus { Accepted = 1, Completed = 2 }

/// <summary>Internal result, not a replacement for the existing public response DTOs.</summary>
public sealed record OperationResult<T>
{
    private OperationResult(Guid operationId, OperationStatus status, T value, Guid? resourceId, Guid? workId)
    {
        if (operationId == Guid.Empty || resourceId == Guid.Empty || workId == Guid.Empty)
            throw new ArgumentException("Operation and result references must be assigned identifiers.");
        OperationId = operationId;
        Status = status;
        Value = value;
        ResourceId = resourceId;
        WorkId = workId;
    }

    public Guid OperationId { get; }
    public OperationStatus Status { get; }
    public T Value { get; }
    public Guid? ResourceId { get; }
    public Guid? WorkId { get; }

    public static OperationResult<T> Accepted(Guid operationId, Guid workId, T value, Guid? resourceId = null) =>
        new(operationId, OperationStatus.Accepted, value, resourceId, workId);

    public static OperationResult<T> Completed(Guid operationId, T value, Guid? resourceId = null) =>
        new(operationId, OperationStatus.Completed, value, resourceId, null);
}
