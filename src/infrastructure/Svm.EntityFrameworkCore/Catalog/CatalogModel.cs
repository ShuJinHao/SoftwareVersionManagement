using Microsoft.EntityFrameworkCore;
using Svm.Core.Releases;
using Svm.Core.Instances;
using Svm.SharedKernel.Domain;

namespace Svm.EntityFrameworkCore.Catalog;

internal sealed class SiteIdentity { public int Singleton { get; set; } = 1; public Guid SiteId { get; set; } }
internal static class CatalogModel
{
    internal static void Configure(ModelBuilder model)
    {
        var site = model.Entity<SiteIdentity>(); site.ToTable("site_identity", "ins", t => t.HasCheckConstraint("CK_site_singleton", "\"Singleton\"=1"));
        site.HasKey(x => x.Singleton); site.Property(x => x.Singleton).ValueGeneratedNever(); site.HasAlternateKey(x => x.SiteId);
        var software = model.Entity<SoftwareProduct>(); software.ToTable("software", "rel"); software.HasKey(x => x.Id);
        software.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<SoftwareProduct>(x)).ValueGeneratedNever(); software.Ignore(x => x.DomainEvents);
        software.Property(x => x.Code).HasMaxLength(64); software.HasIndex(x => x.Code).IsUnique();
        software.Property(x => x.Name).HasMaxLength(128); software.Property(x => x.Category).HasMaxLength(32); software.Property(x => x.Description).HasMaxLength(2000);
        var process = model.Entity<ProductionProcess>(); process.ToTable("processes", "ins"); process.HasKey(x => x.Id);
        process.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<ProductionProcess>(x)).ValueGeneratedNever(); process.Ignore(x => x.DomainEvents);
        process.Property(x => x.Code).HasMaxLength(64); process.Property(x => x.Name).HasMaxLength(128);
        process.HasIndex(x => new { x.SiteId, x.Code }).IsUnique();
        process.HasOne<SiteIdentity>().WithMany().HasForeignKey(x => x.SiteId).HasPrincipalKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
        var device = model.Entity<ProductionDevice>(); device.ToTable("devices", "ins"); device.HasKey(x => x.Id);
        device.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<ProductionDevice>(x)).ValueGeneratedNever(); device.Ignore(x => x.DomainEvents);
        device.Property(x => x.ProcessId).HasConversion(x => x.Value, x => new StrongId<ProductionProcess>(x));
        device.Property(x => x.DeviceNo).HasMaxLength(64); device.Property(x => x.Name).HasMaxLength(128); device.HasIndex(x => x.DeviceNo).IsUnique();
        device.HasOne<ProductionProcess>().WithMany().HasForeignKey(x => x.ProcessId).OnDelete(DeleteBehavior.Restrict);
        var binding = model.Entity<DeviceSoftwareBinding>(); binding.ToTable("device_software_bindings", "ins"); binding.HasKey(x => x.Id);
        binding.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<DeviceSoftwareBinding>(x)).ValueGeneratedNever(); binding.Ignore(x => x.DomainEvents);
        binding.Property(x => x.DeviceId).HasConversion(x => x.Value, x => new StrongId<ProductionDevice>(x));
        binding.HasIndex(x => new { x.DeviceId, x.SoftwareId }).IsUnique(); binding.HasIndex(x => x.SoftwareId);
        binding.HasOne<ProductionDevice>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        // Cross-module IDs remain opaque GUIDs in INS. The migration separately installs a restrictive FK to rel.software.
    }
}
