using Microsoft.EntityFrameworkCore;
namespace Svm.EntityFrameworkCore.Migrations;
// Frozen sixteenth migration model; independent of live mappings.
internal static class RollbackCompatibilityV1Model
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity("Svm.Core.Releases.CompatibilityDeclaration", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever(); b.HasKey("Id");
            b.Property<Guid>("TargetReleaseId"); b.Property<long>("DeclarationRevision");
            b.Property<Guid[]>("SourceReleaseIds").IsRequired(); b.Property<string>("DatabaseMode").IsRequired();
            b.Property<string>("DatabaseRequirementsJson").IsRequired().HasColumnType("jsonb");
            b.Property<string>("Statement").IsRequired(); b.Property<string>("Reason").IsRequired();
            b.Property<Guid>("DeclaredBy"); b.Property<DateTimeOffset>("CreatedAt");
            b.HasIndex("TargetReleaseId", "DeclarationRevision").IsUnique().HasDatabaseName("IX_compatibility_target_revision"); b.ToTable("compatibility_declarations", "rel");
        });
        model.Entity("Svm.Core.Tasks.Deployment").Property<string>("Kind").IsRequired().HasDefaultValue("Update");
        model.Entity("Svm.Core.Tasks.InstanceTask").Property<string>("AdmissionCompatibilityJson").HasColumnType("jsonb");
        model.Entity("Svm.Core.Tasks.InstanceTask").Property<Guid?>("AdmissionCompatibilityId");
        model.Entity("Svm.Core.Tasks.ExecutionAttempt").Property<string>("CompatibilityJson").HasColumnType("jsonb");
        model.Entity("Svm.Core.Tasks.ExecutionAttempt").Property<Guid?>("CompatibilityDeclarationId");
    }
}
