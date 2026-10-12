using Microsoft.EntityFrameworkCore;

namespace Svm.EntityFrameworkCore.Migrations;

// Frozen instance-access migration model.
internal static class InstanceAccessV1Model
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity("Svm.Core.Identity.MachineSubject", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("SoftwareId");
            b.HasKey("Id");
            b.HasIndex("SoftwareId");
            b.ToTable("instance_subjects","iam");
        });
        model.Entity("Svm.Core.Identity.InstanceCredential", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("SubjectId");
            b.Property<string>("SecretHash").IsRequired().HasMaxLength(64);
            b.Property<DateTimeOffset?>("ExpiresAt");
            b.Property<DateTimeOffset?>("RevokedAt");
            b.Property<long>("Revision");
            b.HasKey("Id");
            b.HasOne("Svm.Core.Identity.MachineSubject", null).WithMany().HasForeignKey("SubjectId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasIndex("SubjectId");
            b.ToTable("instance_credentials","iam");
        });
        model.Entity("Svm.Core.Identity.EnrollmentPermission", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("SoftwareId");
            b.Property<Guid[]>("DeviceIds").IsRequired();
            b.Property<string>("SecretHash").IsRequired().HasMaxLength(64);
            b.Property<DateTimeOffset>("ExpiresAt");
            b.Property<int>("MaxInstances");
            b.Property<int>("UsedCount");
            b.Property<DateTimeOffset?>("RevokedAt");
            b.Property<long>("Revision");
            b.HasKey("Id");
            b.HasIndex("SoftwareId");
            b.ToTable("enrollment_grants","iam");
        });
        model.Entity("Svm.Core.Identity.InstanceRegistration", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("GrantId");
            b.Property<Guid>("SoftwareId");
            b.Property<Guid>("InstallationKey");
            b.Property<Guid>("DeviceId");
            b.Property<Guid>("Key");
            b.Property<string>("RequestDigest").IsRequired().HasMaxLength(64);
            b.Property<Guid>("InstanceId");
            b.Property<Guid>("CredentialId");
            b.HasKey("Id");
            b.HasOne("Svm.Core.Identity.EnrollmentPermission", null).WithMany().HasForeignKey("GrantId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Svm.Core.Identity.InstanceCredential", null).WithMany().HasForeignKey("CredentialId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasIndex("SoftwareId","InstallationKey").IsUnique();
            b.HasIndex("GrantId","Key").IsUnique();
            b.HasIndex("InstanceId").IsUnique();
            b.HasIndex("CredentialId");
            b.ToTable("registrations","iam");
        });
        model.Entity("Svm.Core.Identity.RecoveryPermission", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("SoftwareId");
            b.Property<Guid>("InstanceId");
            b.Property<string>("SecretHash").IsRequired().HasMaxLength(64);
            b.Property<DateTimeOffset>("ExpiresAt");
            b.Property<DateTimeOffset?>("RevokedAt");
            b.Property<long>("Revision");
            b.Property<Guid?>("UsedKey");
            b.Property<string>("RequestDigest").HasMaxLength(64);
            b.Property<Guid?>("CredentialId");
            b.Property<long?>("ResultEpoch");
            b.HasKey("Id");
            b.HasOne("Svm.Core.Identity.MachineSubject", null).WithMany().HasForeignKey("InstanceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasIndex("InstanceId");
            b.ToTable("recovery_grants","iam");
        });
        model.Entity("Svm.Core.Instances.ManagedInstance", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("SoftwareId");
            b.Property<Guid>("DeviceId");
            b.Property<Guid>("InstallationKey");
            b.Property<string>("Lifecycle").IsRequired().HasMaxLength(32);
            b.Property<long>("Revision");
            b.HasKey("Id");
            b.HasIndex("SoftwareId","InstallationKey").IsUnique();
            b.HasIndex("DeviceId","SoftwareId","Id");
            b.ToTable("instances","ins");
        });
        model.Entity("Svm.Core.Instances.InstanceSnapshot", b =>
        {
            b.Property<Guid>("InstanceId").ValueGeneratedNever();
            b.Property<Guid>("SoftwareId");
            b.Property<long>("StreamEpoch");
            b.Property<bool>("StreamOpen");
            b.Property<long>("ReportSeq");
            b.Property<DateTimeOffset?>("LastAcceptedAt");
            b.Property<string>("SnapshotJson").HasColumnType("jsonb");
            b.Property<string>("RequestDigest").HasMaxLength(64);
            b.Property<string>("InstallationDigest").HasMaxLength(64);
            b.Property<Guid?>("EvidenceId");
            b.HasKey("InstanceId");
            b.HasOne("Svm.Core.Instances.ManagedInstance", null).WithMany().HasForeignKey("InstanceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasIndex("SoftwareId","LastAcceptedAt","InstanceId");
            b.ToTable("instance_snapshots","ins");
        });
        model.Entity("Svm.Core.Instances.InstallationEvidence", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("InstanceId");
            b.Property<Guid>("SoftwareId");
            b.Property<long>("StreamEpoch");
            b.Property<long>("ReportSeq");
            b.Property<string>("InstallationState").IsRequired().HasMaxLength(32);
            b.Property<Guid?>("InstalledReleaseId");
            b.Property<string>("InstalledVersion").HasMaxLength(128);
            b.Property<DateTimeOffset?>("InstalledAt");
            b.Property<DateTimeOffset>("ReceivedAt");
            b.Property<string>("ReportedRunningState").IsRequired().HasMaxLength(32);
            b.Property<string>("RequestDigest").IsRequired().HasMaxLength(64);
            b.HasKey("Id");
            b.HasOne("Svm.Core.Instances.ManagedInstance", null).WithMany().HasForeignKey("InstanceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasIndex("InstanceId","StreamEpoch","ReportSeq").IsUnique();
            b.HasIndex("InstanceId","ReceivedAt","Id");
            b.ToTable("installation_evidence","ins");
        });
        model.Entity("Svm.Core.Instances.ReportStreamReceipt", b =>
        {
            b.Property<Guid>("OperationId").ValueGeneratedNever();
            b.Property<Guid>("InstanceId");
            b.Property<long>("Epoch");
            b.HasKey("OperationId");
            b.HasOne("Svm.Core.Instances.ManagedInstance", null).WithMany().HasForeignKey("InstanceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasIndex("InstanceId");
            b.ToTable("report_stream_receipts","ins");
        });
    }
}
