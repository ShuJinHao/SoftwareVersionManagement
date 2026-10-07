using Microsoft.EntityFrameworkCore;

namespace Svm.EntityFrameworkCore.Migrations;

// Frozen migration model; no calls to live module mappings.
internal static class CatalogV1Model
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity("Svm.EntityFrameworkCore.Catalog.SiteIdentity", b =>
        {
            b.Property<int>("Singleton").ValueGeneratedNever(); b.Property<Guid>("SiteId");
            b.HasKey("Singleton"); b.HasAlternateKey("SiteId"); b.ToTable("site_identity", "ins", t => t.HasCheckConstraint("CK_site_singleton", "\"Singleton\"=1"));
        });
        model.Entity("Svm.Core.Releases.SoftwareProduct", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever(); b.Property<string>("Code").IsRequired().HasMaxLength(64);
            b.Property<string>("Name").IsRequired().HasMaxLength(128); b.Property<string>("Category").IsRequired().HasMaxLength(32);
            b.Property<string>("Description").HasMaxLength(2000); b.Property<long>("Revision");
            b.HasKey("Id"); b.HasIndex("Code").IsUnique(); b.ToTable("software", "rel");
        });
        model.Entity("Svm.Core.Instances.ProductionProcess", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever(); b.Property<Guid>("SiteId"); b.Property<string>("Code").IsRequired().HasMaxLength(64);
            b.Property<string>("Name").IsRequired().HasMaxLength(128); b.Property<long>("Revision");
            b.HasKey("Id"); b.HasIndex("SiteId", "Code").IsUnique();
            b.HasOne("Svm.EntityFrameworkCore.Catalog.SiteIdentity", null).WithMany().HasForeignKey("SiteId").HasPrincipalKey("SiteId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.ToTable("processes", "ins");
        });
        model.Entity("Svm.Core.Instances.ProductionDevice", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever(); b.Property<Guid>("ProcessId"); b.Property<string>("DeviceNo").IsRequired().HasMaxLength(64);
            b.Property<string>("Name").IsRequired().HasMaxLength(128); b.Property<long>("Revision");
            b.HasKey("Id"); b.HasIndex("ProcessId"); b.HasIndex("DeviceNo").IsUnique();
            b.HasOne("Svm.Core.Instances.ProductionProcess", null).WithMany().HasForeignKey("ProcessId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.ToTable("devices", "ins");
        });
        model.Entity("Svm.Core.Instances.DeviceSoftwareBinding", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever(); b.Property<Guid>("DeviceId"); b.Property<Guid>("SoftwareId");
            b.Property<long>("Revision"); b.Property<bool>("IsActive"); b.Property<bool>("HasInstanceReference");
            b.HasKey("Id"); b.HasIndex("DeviceId", "SoftwareId").IsUnique(); b.HasIndex("SoftwareId");
            b.HasOne("Svm.Core.Instances.ProductionDevice", null).WithMany().HasForeignKey("DeviceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.ToTable("device_software_bindings", "ins");
        });
    }
}
