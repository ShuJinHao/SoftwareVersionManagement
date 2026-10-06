namespace Svm.Core.Audit;

public sealed class AuditEntry
{
    public Guid Id { get; init; }
    public Guid OperationId { get; init; }
    public Guid? SubjectId { get; init; }
    public string ActorKind { get; init; } = "";
    public string? EmployeeNo { get; init; }
    public string? DisplayName { get; init; }
    public string Operation { get; init; } = "";
    public Guid? ObjectId { get; init; }
    public string Result { get; init; } = "";
    public string Reason { get; init; } = "";
    public string CorrelationId { get; init; } = "";
    public DateTimeOffset OccurredAt { get; init; }
}

public interface IAuditRepository { void Append(AuditEntry entry); }
