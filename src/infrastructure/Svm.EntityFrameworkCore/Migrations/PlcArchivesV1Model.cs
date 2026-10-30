using Microsoft.EntityFrameworkCore;
namespace Svm.EntityFrameworkCore.Migrations;
// Frozen fifteenth migration model; independent of live mappings.
internal static class PlcArchivesV1Model
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity("Svm.Core.Identity.PlcUploadCredential", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("SoftwareId");
            b.Property<string>("Name").IsRequired().HasMaxLength(128);
            b.Property<string>("SecretHash").IsRequired().HasMaxLength(256);
            b.Property<string>("ScopeJson").IsRequired().HasColumnType("jsonb");
            b.Property<DateTimeOffset>("ExpiresAt");
            b.Property<Guid>("CreatedBy");
            b.Property<DateTimeOffset>("CreatedAt");
            b.Property<DateTimeOffset?>("RevokedAt");
            b.Property<long>("Revision");
            b.HasKey("Id"); b.ToTable("plc_upload_credentials", "iam");
            b.HasIndex("SoftwareId", "Id");
        });
        model.Entity("Svm.Core.Instances.PlcUsageRecord", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("HardwareId");
            b.Property<Guid>("BindingId");
            b.Property<long>("BindingRevision");
            b.Property<Guid>("SoftwareId");
            b.Property<Guid>("ReleaseId");
            b.Property<string>("Version").IsRequired().HasMaxLength(128);
            b.Property<string>("ReleaseState").IsRequired().HasMaxLength(32);
            b.Property<Guid?>("PreviousReleaseId");
            b.Property<DateTimeOffset>("FieldAt");
            b.Property<DateTimeOffset>("RecordedAt");
            b.Property<Guid>("ActorId");
            b.Property<string>("EmployeeNo").IsRequired().HasMaxLength(64);
            b.Property<string>("Note").IsRequired().HasMaxLength(2000);
            b.Property<string>("Materials").IsRequired().HasMaxLength(2000);
            b.Property<Guid?>("CorrectsId");
            b.HasKey("Id"); b.ToTable("plc_usage_records", "ins");
            b.HasIndex("HardwareId", "Id");
            b.HasIndex("BindingId", "BindingRevision", "FieldAt");
            b.HasIndex("CorrectsId").IsUnique();
        });
        model.Entity("Svm.Core.Instances.PlcUsageState", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<long>("BindingRevision");
            b.Property<long>("Revision");
            b.Property<Guid?>("CurrentRecordId");
            b.HasKey("Id"); b.ToTable("plc_usage_state", "ins");
        });
        model.Entity("Svm.Core.Releases.PlcPublication", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("ReleaseId");
            b.Property<Guid>("HardwareId");
            b.Property<Guid>("BindingId");
            b.Property<long>("BindingRevision");
            b.Property<DateTimeOffset>("FieldAt");
            b.Property<string>("Verification").IsRequired().HasMaxLength(2000);
            b.Property<string>("Materials").IsRequired().HasMaxLength(2000);
            b.Property<Guid>("ActorId");
            b.Property<string>("EmployeeNo").IsRequired().HasMaxLength(64);
            b.Property<DateTimeOffset>("RecordedAt");
            b.HasKey("Id"); b.ToTable("plc_publications", "rel");
            b.HasIndex("ReleaseId").IsUnique();
        });
        model.Entity("Svm.Core.Instances.HardwareSoftwareBinding", b =>
        {
            b.Property<string>("HardwareType").IsRequired().HasMaxLength(8).HasDefaultValue("IPC");
            b.HasIndex("HardwareId").IsUnique().HasFilter("\"IsActive\" AND \"HardwareType\"='PLC'");
        });
        model.Entity("Svm.Core.Releases.SoftwareRelease", b =>
        { b.Property<Guid?>("PlcEvidenceId"); b.Property<Guid?>("PlcUploadBindingId"); b.Property<long?>("PlcUploadBindingRevision"); });
        model.Entity("Svm.Core.Packages.PackageDownload", b =>
        { b.Property<Guid?>("PlcBindingId"); b.Property<long?>("PlcBindingRevision"); });
    }
}
