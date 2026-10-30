using Microsoft.EntityFrameworkCore;
using Svm.Core.Identity;
using Svm.Core.Instances;
using Svm.Core.Releases;
using Svm.SharedKernel.Domain;
namespace Svm.EntityFrameworkCore.Plc;

internal static class PlcModel
{
    internal static void Configure(ModelBuilder model)
    {
        var credential = model.Entity<PlcUploadCredential>(); credential.ToTable("plc_upload_credentials", "iam"); credential.HasKey(x => x.Id);
        credential.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<PlcUploadCredential>(x)).ValueGeneratedNever(); credential.Ignore(x => x.DomainEvents);
        credential.Property(x => x.Name).HasMaxLength(128); credential.Property(x => x.SecretHash).HasMaxLength(256);
        credential.Property(x => x.ScopeJson).HasColumnType("jsonb"); credential.HasIndex(x => new { x.SoftwareId, x.Id });
        var usage = model.Entity<PlcUsageRecord>(); usage.ToTable("plc_usage_records", "ins"); usage.HasKey(x => x.Id); usage.Property(x => x.Id).ValueGeneratedNever();
        usage.Property(x => x.Version).HasMaxLength(128); usage.Property(x => x.ReleaseState).HasMaxLength(32);
        usage.Property(x => x.EmployeeNo).HasMaxLength(64); usage.Property(x => x.Note).HasMaxLength(2000); usage.Property(x => x.Materials).HasMaxLength(2000);
        usage.HasIndex(x => new { x.HardwareId, x.Id }); usage.HasIndex(x => new { x.BindingId, x.BindingRevision, x.FieldAt }); usage.HasIndex(x => x.CorrectsId).IsUnique();
        var state = model.Entity<PlcUsageState>(); state.ToTable("plc_usage_state", "ins"); state.HasKey(x => x.Id); state.Ignore(x => x.DomainEvents);
        state.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<PlcUsageState>(x)).ValueGeneratedNever();
        var evidence = model.Entity<PlcPublication>(); evidence.ToTable("plc_publications", "rel"); evidence.HasKey(x => x.Id); evidence.Property(x => x.Id).ValueGeneratedNever();
        evidence.Property(x => x.Verification).HasMaxLength(2000); evidence.Property(x => x.Materials).HasMaxLength(2000); evidence.Property(x => x.EmployeeNo).HasMaxLength(64);
        evidence.HasIndex(x => x.ReleaseId).IsUnique();
        model.Entity<HardwareSoftwareBinding>().Property(x => x.HardwareType).HasMaxLength(8).HasDefaultValue("IPC");
        model.Entity<HardwareSoftwareBinding>().HasIndex(x => x.HardwareId).IsUnique().HasFilter("\"IsActive\" AND \"HardwareType\"='PLC'");
        model.Entity<SoftwareRelease>().Property(x => x.PlcEvidenceId);
    }
}
