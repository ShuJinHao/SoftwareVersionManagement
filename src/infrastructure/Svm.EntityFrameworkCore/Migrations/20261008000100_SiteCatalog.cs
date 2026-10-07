using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;

namespace Svm.EntityFrameworkCore.Migrations;

[DbContext(typeof(SvmDbContext))]
[Migration("20261008000100_SiteCatalog")]
internal sealed class SiteCatalogMigration : Migration
{
    public SiteCatalogMigration() { }
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE ins.site_identity ("Singleton" integer NOT NULL, "SiteId" uuid NOT NULL,
          CONSTRAINT "PK_site_identity" PRIMARY KEY ("Singleton"), CONSTRAINT "AK_site_identity_SiteId" UNIQUE ("SiteId"),
          CONSTRAINT "CK_site_singleton" CHECK ("Singleton"=1));
        CREATE TABLE rel.software ("Id" uuid NOT NULL, "Code" varchar(64) NOT NULL, "Name" varchar(128) NOT NULL,
          "Category" varchar(32) NOT NULL, "Description" varchar(2000), "Revision" bigint NOT NULL,
          CONSTRAINT "PK_software" PRIMARY KEY ("Id"));
        CREATE UNIQUE INDEX "IX_software_Code" ON rel.software ("Code");
        CREATE TABLE ins.processes ("Id" uuid NOT NULL, "SiteId" uuid NOT NULL, "Code" varchar(64) NOT NULL,
          "Name" varchar(128) NOT NULL, "Revision" bigint NOT NULL, CONSTRAINT "PK_processes" PRIMARY KEY ("Id"),
          CONSTRAINT "FK_processes_site_identity_SiteId" FOREIGN KEY ("SiteId") REFERENCES ins.site_identity ("SiteId") ON DELETE RESTRICT);
        CREATE UNIQUE INDEX "IX_processes_SiteId_Code" ON ins.processes ("SiteId","Code");
        CREATE TABLE ins.devices ("Id" uuid NOT NULL, "ProcessId" uuid NOT NULL, "DeviceNo" varchar(64) NOT NULL,
          "Name" varchar(128) NOT NULL, "Revision" bigint NOT NULL, CONSTRAINT "PK_devices" PRIMARY KEY ("Id"),
          CONSTRAINT "FK_devices_processes_ProcessId" FOREIGN KEY ("ProcessId") REFERENCES ins.processes ("Id") ON DELETE RESTRICT);
        CREATE INDEX "IX_devices_ProcessId" ON ins.devices ("ProcessId");
        CREATE UNIQUE INDEX "IX_devices_DeviceNo" ON ins.devices ("DeviceNo");
        CREATE TABLE ins.device_software_bindings ("Id" uuid NOT NULL, "DeviceId" uuid NOT NULL, "SoftwareId" uuid NOT NULL,
          "Revision" bigint NOT NULL, "IsActive" boolean NOT NULL, "HasInstanceReference" boolean NOT NULL,
          CONSTRAINT "PK_device_software_bindings" PRIMARY KEY ("Id"),
          CONSTRAINT "FK_device_software_bindings_devices_DeviceId" FOREIGN KEY ("DeviceId") REFERENCES ins.devices ("Id") ON DELETE RESTRICT,
          CONSTRAINT "FK_device_software_bindings_software_SoftwareId" FOREIGN KEY ("SoftwareId") REFERENCES rel.software ("Id") ON DELETE RESTRICT);
        CREATE UNIQUE INDEX "IX_device_software_bindings_DeviceId_SoftwareId" ON ins.device_software_bindings ("DeviceId","SoftwareId");
        CREATE INDEX "IX_device_software_bindings_SoftwareId" ON ins.device_software_bindings ("SoftwareId");
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Site and software history require an explicit recovery plan; automatic downgrade is disabled.");
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        SvmDbContextModelSnapshot.Configure(modelBuilder); SvmDbContextModelSnapshot.ConfigurePersonnelV1(modelBuilder);
        SvmDbContextModelSnapshot.ConfigureOperationResultsV1(modelBuilder); BusOutboxV1Model.Configure(modelBuilder); CatalogV1Model.Configure(modelBuilder);
    }
}
