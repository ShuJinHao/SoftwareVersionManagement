using Microsoft.EntityFrameworkCore;

namespace Svm.EntityFrameworkCore.Migrations;

// Frozen twelfth migration additions, independent of live mappings and previous frozen models.
internal static class HardwareWorkspaceV1Model
{
    internal static void Configure(ModelBuilder model)
    {
        model.HasSequence<long>("process_code_seq", "ins");
        model.HasSequence<long>("device_no_seq", "ins");
        model.HasSequence<long>("software_code_seq", "rel");
        model.Entity("Svm.Core.Instances.ProductionDevice", b => b.Property<string>("AssetNumber").HasMaxLength(128));
        model.Entity("Svm.Core.Instances.HardwareAsset", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever(); b.Property<Guid>("DeviceId");
            b.Property<string>("Type").IsRequired().HasMaxLength(8); b.Property<string>("Name").IsRequired().HasMaxLength(128);
            b.Property<string>("AssetNumber").HasMaxLength(128); b.Property<string>("ManagementIp").HasMaxLength(64); b.Property<long>("Revision");
            b.HasKey("Id"); b.HasIndex("DeviceId", "Id");
            b.HasOne("Svm.Core.Instances.ProductionDevice", null).WithMany().HasForeignKey("DeviceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.ToTable("hardware", "ins", t => t.HasCheckConstraint("CK_hardware_type", "\"Type\" IN ('IPC','PLC')"));
        });
        model.Entity("Svm.Core.Instances.HardwareSoftwareBinding", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever(); b.Property<Guid>("HardwareId"); b.Property<Guid>("DeviceBindingId"); b.Property<Guid>("SoftwareId");
            b.Property<bool>("IsActive"); b.Property<long>("Revision"); b.Property<Guid?>("InstanceId"); b.HasKey("Id");
            b.HasIndex("HardwareId", "SoftwareId").IsUnique(); b.HasIndex("DeviceBindingId"); b.HasIndex("InstanceId").IsUnique(); b.HasIndex("SoftwareId", "Id");
            b.HasOne("Svm.Core.Instances.HardwareAsset", null).WithMany().HasForeignKey("HardwareId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Svm.Core.Instances.DeviceSoftwareBinding", null).WithMany().HasForeignKey("DeviceBindingId").HasConstraintName("FK_hardware_bindings_device_binding").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.ToTable("hardware_software_bindings", "ins");
        });
        model.Entity("Svm.Core.Instances.ManagedInstance", b => { b.Property<Guid?>("HardwareBindingId"); b.HasIndex("HardwareBindingId").IsUnique(); });
        model.Entity("Svm.Core.Identity.EnrollmentPermission", b => { b.Property<Guid?>("HardwareBindingId"); b.Property<long?>("HardwareBindingRevision"); b.HasIndex("HardwareBindingId"); });
        model.Entity("Svm.Core.Tasks.InstanceTask", b => { b.Property<Guid?>("HardwareBindingId"); b.HasIndex("HardwareBindingId"); });
    }
}
