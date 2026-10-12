using Microsoft.EntityFrameworkCore;
using Svm.Core.Identity;
using Svm.Core.Instances;
using Svm.SharedKernel.Domain;

namespace Svm.EntityFrameworkCore.Instances;

internal static class InstanceModel
{
    internal static void Configure(ModelBuilder m)
    {
        var subject=m.Entity<MachineSubject>(); subject.ToTable("instance_subjects","iam"); subject.Property(x=>x.Id).HasConversion(x=>x.Value,x=>new StrongId<MachineSubject>(x)); subject.HasKey(x=>x.Id); subject.Property(x=>x.Id).ValueGeneratedNever(); subject.Ignore(x=>x.DomainEvents); subject.HasIndex(x=>x.SoftwareId);
        var c=m.Entity<InstanceCredential>(); c.ToTable("instance_credentials","iam"); c.Property(x=>x.SubjectId).HasConversion(x=>x.Value,x=>new StrongId<MachineSubject>(x)); c.HasKey(x=>x.Id); c.Property(x=>x.Id).ValueGeneratedNever(); c.Property(x=>x.SecretHash).HasMaxLength(64); c.HasIndex(x=>x.SubjectId);
        c.HasOne<MachineSubject>().WithMany().HasForeignKey(x=>x.SubjectId).OnDelete(DeleteBehavior.Restrict);
        var g=m.Entity<EnrollmentPermission>(); g.ToTable("enrollment_grants","iam"); g.Property(x=>x.Id).HasConversion(x=>x.Value,x=>new StrongId<EnrollmentPermission>(x)); g.HasKey(x=>x.Id); g.Property(x=>x.Id).ValueGeneratedNever(); g.Ignore(x=>x.DomainEvents); g.Property(x=>x.SecretHash).HasMaxLength(64); g.HasIndex(x=>x.SoftwareId);
        var r=m.Entity<InstanceRegistration>(); r.ToTable("registrations","iam"); r.Property(x=>x.GrantId).HasConversion(x=>x.Value,x=>new StrongId<EnrollmentPermission>(x)); r.HasKey(x=>x.Id); r.Property(x=>x.Id).ValueGeneratedNever(); r.Property(x=>x.RequestDigest).HasMaxLength(64);
        r.HasIndex(x=>new { x.SoftwareId,x.InstallationKey }).IsUnique(); r.HasIndex(x=>new { x.GrantId,x.Key }).IsUnique(); r.HasIndex(x=>x.InstanceId).IsUnique();
        r.HasOne<EnrollmentPermission>().WithMany().HasForeignKey(x=>x.GrantId).OnDelete(DeleteBehavior.Restrict);
        r.HasOne<InstanceCredential>().WithMany().HasForeignKey(x=>x.CredentialId).OnDelete(DeleteBehavior.Restrict);
        var rg=m.Entity<RecoveryPermission>(); rg.ToTable("recovery_grants","iam"); rg.Property(x=>x.Id).HasConversion(x=>x.Value,x=>new StrongId<RecoveryPermission>(x)); rg.Property(x=>x.InstanceId).HasConversion(x=>x.Value,x=>new StrongId<MachineSubject>(x)); rg.HasKey(x=>x.Id); rg.Property(x=>x.Id).ValueGeneratedNever(); rg.Ignore(x=>x.DomainEvents); rg.Property(x=>x.SecretHash).HasMaxLength(64); rg.Property(x=>x.RequestDigest).HasMaxLength(64);
        rg.HasIndex(x=>x.InstanceId); rg.HasOne<MachineSubject>().WithMany().HasForeignKey(x=>x.InstanceId).OnDelete(DeleteBehavior.Restrict);
        var i=m.Entity<ManagedInstance>(); i.ToTable("instances","ins"); i.Property(x=>x.Id).HasConversion(x=>x.Value,x=>new StrongId<ManagedInstance>(x)); i.HasKey(x=>x.Id); i.Property(x=>x.Id).ValueGeneratedNever(); i.Ignore(x=>x.DomainEvents); i.Property(x=>x.Lifecycle).HasMaxLength(32); i.HasIndex(x=>new { x.SoftwareId,x.InstallationKey }).IsUnique(); i.HasIndex(x=>new { x.DeviceId,x.SoftwareId,x.Id });
        var s=m.Entity<InstanceSnapshot>(); s.ToTable("instance_snapshots","ins"); s.Property(x=>x.InstanceId).HasConversion(x=>x.Value,x=>new StrongId<ManagedInstance>(x)); s.HasKey(x=>x.InstanceId); s.Property(x=>x.InstanceId).ValueGeneratedNever(); s.Property(x=>x.SnapshotJson).HasColumnType("jsonb"); s.Property(x=>x.RequestDigest).HasMaxLength(64); s.Property(x=>x.InstallationDigest).HasMaxLength(64);
        s.HasIndex(x=>new { x.SoftwareId,x.LastAcceptedAt,x.InstanceId }); s.HasOne<ManagedInstance>().WithMany().HasForeignKey(x=>x.InstanceId).OnDelete(DeleteBehavior.Restrict);
        var e=m.Entity<InstallationEvidence>(); e.ToTable("installation_evidence","ins"); e.Property(x=>x.InstanceId).HasConversion(x=>x.Value,x=>new StrongId<ManagedInstance>(x)); e.HasKey(x=>x.Id); e.Property(x=>x.Id).ValueGeneratedNever(); e.Property(x=>x.InstallationState).HasMaxLength(32); e.Property(x=>x.InstalledVersion).HasMaxLength(128); e.Property(x=>x.ReportedRunningState).HasMaxLength(32); e.Property(x=>x.RequestDigest).HasMaxLength(64);
        e.HasIndex(x=>new { x.InstanceId,x.StreamEpoch,x.ReportSeq }).IsUnique(); e.HasIndex(x=>new { x.InstanceId,x.ReceivedAt,x.Id }); e.HasOne<ManagedInstance>().WithMany().HasForeignKey(x=>x.InstanceId).OnDelete(DeleteBehavior.Restrict);
        var sr=m.Entity<ReportStreamReceipt>(); sr.ToTable("report_stream_receipts","ins"); sr.Property(x=>x.InstanceId).HasConversion(x=>x.Value,x=>new StrongId<ManagedInstance>(x)); sr.HasKey(x=>x.OperationId); sr.Property(x=>x.OperationId).ValueGeneratedNever(); sr.HasOne<ManagedInstance>().WithMany().HasForeignKey(x=>x.InstanceId).OnDelete(DeleteBehavior.Restrict);
    }
}
