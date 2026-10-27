using Microsoft.EntityFrameworkCore;

namespace Svm.EntityFrameworkCore.Migrations;

internal static class GitLabSourceV1Model
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity("Svm.Core.Releases.ReleaseSourceBinding", b =>
        {
            b.Property<Guid?>("ConnectionId"); b.Property<bool>("Managed").HasDefaultValue(false);
            b.Property<bool>("IsCurrent").HasDefaultValue(true); b.Property<bool>("Enabled").HasDefaultValue(true);
            b.Property<long>("Revision").HasDefaultValue(1L); b.Property<long>("SourceRevision").HasDefaultValue(1L);
            b.Metadata.RemoveIndex(b.Metadata.FindIndex(new[] { b.Metadata.FindProperty("SoftwareId")! })!);
            b.HasIndex("SoftwareId", "IsCurrent").IsUnique().HasFilter("\"IsCurrent\"");
        });
        model.Entity("Svm.Core.Releases.ReleaseSourceChange", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever(); b.Property<Guid>("BindingId"); b.Property<long>("Revision");
            b.Property<bool>("Enabled"); b.Property<DateTimeOffset>("CreatedAt");
            b.HasKey("Id"); b.HasIndex("BindingId", "Revision").IsUnique(); b.ToTable("source_changes", "rel");
            b.HasOne("Svm.Core.Releases.ReleaseSourceBinding").WithMany().HasForeignKey("BindingId").OnDelete(DeleteBehavior.Restrict).HasConstraintName("source_changes_BindingId_fkey");
        });
    }
}
