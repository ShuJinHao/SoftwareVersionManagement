using Microsoft.Extensions.DependencyInjection;
using Svm.Core.Audit;
using Svm.Services.Contracts.Audit;

namespace Svm.AuditService;

public static class AuditRegistration
{
    public static IServiceCollection AddSvmAudit(this IServiceCollection services) => services.AddScoped<IAuditWriter, AuditWriter>();
}
internal sealed class AuditWriter(IAuditRepository repository) : IAuditWriter
{
    public void Append(AuditFact fact) => repository.Append(new AuditEntry
    {
        Id = Guid.NewGuid(), OperationId = fact.OperationId, SubjectId = fact.SubjectId, ActorKind = fact.ActorKind,
        EmployeeNo = fact.EmployeeNo, DisplayName = fact.DisplayName, Operation = fact.Operation, ObjectId = fact.ObjectId,
        Result = fact.Result, Reason = fact.Reason, CorrelationId = fact.CorrelationId, OccurredAt = DateTimeOffset.UtcNow
    });
}
