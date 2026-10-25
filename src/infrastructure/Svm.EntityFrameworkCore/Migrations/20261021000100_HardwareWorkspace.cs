using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;

namespace Svm.EntityFrameworkCore.Migrations;

[DbContext(typeof(SvmDbContext))]
[Migration("20261021000100_HardwareWorkspace")]
internal sealed class HardwareWorkspaceMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE SEQUENCE ins.process_code_seq AS bigint START WITH 1;
        CREATE SEQUENCE ins.device_no_seq AS bigint START WITH 1;
        CREATE SEQUENCE rel.software_code_seq AS bigint START WITH 1;
        ALTER TABLE ins.devices ADD COLUMN "AssetNumber" varchar(128);
        -- REL software and INS mappings can now be created by the same root operation.
        -- Keep the opaque module references; enforce them at the common commit boundary.
        ALTER TABLE ins.device_software_bindings ALTER CONSTRAINT "FK_device_software_bindings_software_SoftwareId" DEFERRABLE INITIALLY DEFERRED;
        CREATE TABLE ins.hardware (
          "Id" uuid PRIMARY KEY, "DeviceId" uuid NOT NULL, "Type" varchar(8) NOT NULL,
          "Name" varchar(128) NOT NULL, "AssetNumber" varchar(128), "ManagementIp" varchar(64), "Revision" bigint NOT NULL,
          CONSTRAINT "CK_hardware_type" CHECK ("Type" IN ('IPC','PLC')),
          CONSTRAINT "FK_hardware_devices_DeviceId" FOREIGN KEY ("DeviceId") REFERENCES ins.devices("Id") ON DELETE RESTRICT);
        CREATE INDEX "IX_hardware_DeviceId_Id" ON ins.hardware("DeviceId","Id");
        CREATE TABLE ins.hardware_software_bindings (
          "Id" uuid PRIMARY KEY, "HardwareId" uuid NOT NULL, "DeviceBindingId" uuid NOT NULL,
          "SoftwareId" uuid NOT NULL, "IsActive" boolean NOT NULL, "Revision" bigint NOT NULL, "InstanceId" uuid,
          CONSTRAINT "FK_hardware_software_bindings_hardware_HardwareId" FOREIGN KEY ("HardwareId") REFERENCES ins.hardware("Id") ON DELETE RESTRICT,
          CONSTRAINT "FK_hardware_bindings_device_binding" FOREIGN KEY ("DeviceBindingId") REFERENCES ins.device_software_bindings("Id") ON DELETE RESTRICT,
          FOREIGN KEY ("SoftwareId") REFERENCES rel.software("Id") ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED,
          FOREIGN KEY ("InstanceId") REFERENCES ins.instances("Id") DEFERRABLE INITIALLY DEFERRED);
        CREATE UNIQUE INDEX "IX_hardware_software_bindings_HardwareId_SoftwareId" ON ins.hardware_software_bindings("HardwareId","SoftwareId");
        CREATE UNIQUE INDEX "IX_hardware_software_bindings_InstanceId" ON ins.hardware_software_bindings("InstanceId");
        CREATE INDEX "IX_hardware_software_bindings_DeviceBindingId" ON ins.hardware_software_bindings("DeviceBindingId");
        CREATE INDEX "IX_hardware_software_bindings_SoftwareId_Id" ON ins.hardware_software_bindings("SoftwareId","Id");
        ALTER TABLE ins.instances ADD COLUMN "HardwareBindingId" uuid REFERENCES ins.hardware_software_bindings("Id") ON DELETE RESTRICT;
        CREATE UNIQUE INDEX "IX_instances_HardwareBindingId" ON ins.instances("HardwareBindingId");
        ALTER TABLE iam.enrollment_grants ADD COLUMN "HardwareBindingId" uuid REFERENCES ins.hardware_software_bindings("Id") ON DELETE RESTRICT,
          ADD COLUMN "HardwareBindingRevision" bigint,
          ADD CONSTRAINT "CK_enrollment_hardware_scope" CHECK (("HardwareBindingId" IS NULL AND "HardwareBindingRevision" IS NULL) OR
            ("HardwareBindingId" IS NOT NULL AND "HardwareBindingRevision" IS NOT NULL AND "HardwareBindingRevision">0 AND "MaxInstances"=1 AND cardinality("DeviceIds")=1));
        CREATE INDEX "IX_enrollment_grants_HardwareBindingId" ON iam.enrollment_grants("HardwareBindingId");
        ALTER TABLE tsk.tasks ADD COLUMN "HardwareBindingId" uuid REFERENCES ins.hardware_software_bindings("Id") ON DELETE RESTRICT;
        CREATE INDEX "IX_tasks_HardwareBindingId" ON tsk.tasks("HardwareBindingId");
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Hardware references and installation history must be retained.");
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        SvmDbContextModelSnapshot.Configure(modelBuilder);
        SvmDbContextModelSnapshot.ConfigurePersonnelV1(modelBuilder);
        SvmDbContextModelSnapshot.ConfigureOperationResultsV1(modelBuilder);
        BusOutboxV1Model.Configure(modelBuilder); CatalogV1Model.Configure(modelBuilder);
        InstanceAccessV1Model.Configure(modelBuilder); ReleasePackagesV1Model.Configure(modelBuilder);
        ReleasePublicationV1Model.Configure(modelBuilder); TaskWorkflowV1Model.Configure(modelBuilder);
        IntegrationMaterialsV1Model.Configure(modelBuilder); GitLabImportV1Model.Configure(modelBuilder);
        HardwareWorkspaceV1Model.Configure(modelBuilder);
    }
}
