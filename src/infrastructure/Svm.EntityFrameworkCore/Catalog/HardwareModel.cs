using Microsoft.EntityFrameworkCore;
using Svm.Core.Instances;
using Svm.SharedKernel.Domain;

namespace Svm.EntityFrameworkCore.Catalog;

internal static class HardwareModel
{
    internal static void Configure(ModelBuilder model)
    {
        model.HasSequence<long>("process_code_seq", "ins");
        model.HasSequence<long>("device_no_seq", "ins");
        model.HasSequence<long>("software_code_seq", "rel");
        var hardware = model.Entity<HardwareAsset>();
        hardware.ToTable("hardware", "ins", t => t.HasCheckConstraint("CK_hardware_type", "\"Type\" IN ('IPC','PLC')"));
        hardware.HasKey(x => x.Id); hardware.Ignore(x => x.DomainEvents);
        hardware.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<HardwareAsset>(x)).ValueGeneratedNever();
        hardware.Property(x => x.DeviceId).HasConversion(x => x.Value, x => new StrongId<ProductionDevice>(x));
        hardware.Property(x => x.Type).HasMaxLength(8); hardware.Property(x => x.Name).HasMaxLength(128);
        hardware.Property(x => x.AssetNumber).HasMaxLength(128); hardware.Property(x => x.ManagementIp).HasMaxLength(64);
        hardware.HasOne<ProductionDevice>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        hardware.HasIndex(x => new { x.DeviceId, x.Id });
        var binding = model.Entity<HardwareSoftwareBinding>();
        binding.ToTable("hardware_software_bindings", "ins"); binding.HasKey(x => x.Id); binding.Ignore(x => x.DomainEvents);
        binding.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<HardwareSoftwareBinding>(x)).ValueGeneratedNever();
        binding.Property(x => x.HardwareId).HasConversion(x => x.Value, x => new StrongId<HardwareAsset>(x));
        binding.Property(x => x.DeviceBindingId).HasConversion(x => x.Value, x => new StrongId<DeviceSoftwareBinding>(x));
        binding.HasOne<HardwareAsset>().WithMany().HasForeignKey(x => x.HardwareId).OnDelete(DeleteBehavior.Restrict);
        binding.HasOne<DeviceSoftwareBinding>().WithMany().HasForeignKey(x => x.DeviceBindingId).HasConstraintName("FK_hardware_bindings_device_binding").OnDelete(DeleteBehavior.Restrict);
        binding.HasIndex(x => new { x.HardwareId, x.SoftwareId }).IsUnique();
        binding.HasIndex(x => x.InstanceId).IsUnique();
        binding.HasIndex(x => new { x.SoftwareId, x.Id });
        model.Entity<ManagedInstance>().HasIndex(x => x.HardwareBindingId).IsUnique();
        model.Entity<Svm.Core.Identity.EnrollmentPermission>().HasIndex(x => x.HardwareBindingId);
        model.Entity<Svm.Core.Tasks.InstanceTask>().HasIndex(x => x.HardwareBindingId);
        // Opaque cross-aggregate GUID references are enforced by restrictive database FKs in the migration.

    }
}
