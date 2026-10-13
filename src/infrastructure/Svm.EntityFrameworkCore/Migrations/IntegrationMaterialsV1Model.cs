using Microsoft.EntityFrameworkCore;

namespace Svm.EntityFrameworkCore.Migrations;

internal static class IntegrationMaterialsV1Model
{
    internal static void Configure(ModelBuilder m)
    { m.Entity("Svm.Core.Releases.IntegrationMaterial", b => {
        b.Property<Guid>("Id").ValueGeneratedNever(); b.Property<Guid>("ReleaseId"); b.Property<long>("Revision");
        b.Property<string[]>("DataLocations").IsRequired(); b.Property<string>("UpdateBehavior").IsRequired(); b.Property<string>("RollbackBehavior").IsRequired();
        b.Property<string>("RecoveryPlan").IsRequired(); b.Property<string>("VerificationConclusion"); b.Property<string[]>("EvidenceReferences").IsRequired();
        b.Property<string>("Reason").IsRequired(); b.Property<Guid>("RecordedBy"); b.Property<DateTimeOffset>("RecordedAt");
        b.HasKey("Id"); b.HasIndex("ReleaseId", "Revision").IsUnique(); b.ToTable("integration_materials", "rel");
    }); }
}
