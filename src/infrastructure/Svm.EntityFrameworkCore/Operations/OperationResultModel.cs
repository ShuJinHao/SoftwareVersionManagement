using Microsoft.EntityFrameworkCore;

namespace Svm.EntityFrameworkCore.Operations;

internal static class OperationResultModel
{
    internal static void Configure(ModelBuilder model)
    {
        foreach (var schema in new[] { "iam", "rel", "pkg", "ins", "tsk", "aud" })
            model.SharedTypeEntity<Dictionary<string, object>>("Svm.OperationResult." + schema, b =>
            {
                b.IndexerProperty<short>("ActorKind"); b.IndexerProperty<Guid>("SubjectId").ValueGeneratedNever();
                b.IndexerProperty<string>("Operation").IsRequired().HasMaxLength(128);
                b.IndexerProperty<Guid>("IdempotencyKey").ValueGeneratedNever();
                b.IndexerProperty<string>("RequestDigest").IsRequired().HasMaxLength(64);
                b.IndexerProperty<Guid>("OperationId").ValueGeneratedNever();
                b.IndexerProperty<short?>("Status"); b.IndexerProperty<Guid?>("ResourceId"); b.IndexerProperty<Guid?>("WorkId");
                b.IndexerProperty<DateTimeOffset>("CreatedAt").HasDefaultValueSql("clock_timestamp()");
                b.IndexerProperty<DateTimeOffset?>("CompletedAt");
                b.HasKey("ActorKind", "SubjectId", "Operation", "IdempotencyKey");
                b.HasIndex("OperationId").IsUnique().HasDatabaseName("IX_operation_results_OperationId");
                b.ToTable("operation_results", schema);
            });
    }
}
