namespace Svm.Services.Contracts.Audit;

public sealed record AuditFact(Guid OperationId, Guid? SubjectId, string ActorKind, string? EmployeeNo,
    string? DisplayName, string Operation, Guid? ObjectId, string Result, string Reason, string CorrelationId);
public interface IAuditWriter { void Append(AuditFact fact); }
