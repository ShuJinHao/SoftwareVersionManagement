using Microsoft.EntityFrameworkCore;

namespace Svm.EntityFrameworkCore.Migrations;

// Frozen publication additions; the preceding eight target models remain unchanged.
internal static class ReleasePublicationV1Model
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity("Svm.Core.Releases.SoftwareRelease", b =>
        {
            b.Property<Guid?>("PublishedBy");
            b.Property<string>("PublishedEmployeeNo").HasMaxLength(64);
            b.Property<DateTimeOffset?>("PublishedAt");
            b.Property<Guid?>("TestEvidenceId");
            b.Property<string>("PublishReason").HasMaxLength(256);
            b.Property<string>("PublishConclusion").HasMaxLength(2000);
            b.HasIndex("SoftwareId", "PublishedAt");
        });
        model.Entity("Svm.Core.Instances.InstallationEvidence", b =>
            b.HasIndex("Id", "InstalledReleaseId", "SoftwareId").IsUnique());
    }
}
