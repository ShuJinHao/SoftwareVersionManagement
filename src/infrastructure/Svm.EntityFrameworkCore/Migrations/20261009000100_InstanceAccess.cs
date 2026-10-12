using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;

namespace Svm.EntityFrameworkCore.Migrations;

[DbContext(typeof(SvmDbContext))]
[Migration("20261009000100_InstanceAccess")]
internal sealed class InstanceAccessMigration : Migration
{
    public InstanceAccessMigration() { }
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE iam.instance_subjects (
          "Id" uuid NOT NULL,
          "SoftwareId" uuid NOT NULL,
          CONSTRAINT "PK_instance_subjects" PRIMARY KEY ("Id"));
        CREATE INDEX "IX_instance_subjects_SoftwareId" ON iam.instance_subjects ("SoftwareId");
        CREATE TABLE iam.instance_credentials (
          "Id" uuid NOT NULL,
          "SubjectId" uuid NOT NULL,
          "SecretHash" varchar(64) NOT NULL,
          "ExpiresAt" timestamp with time zone,
          "RevokedAt" timestamp with time zone,
          "Revision" bigint NOT NULL,
          CONSTRAINT "PK_instance_credentials" PRIMARY KEY ("Id"),
          CONSTRAINT "FK_instance_credentials_instance_subjects_SubjectId" FOREIGN KEY ("SubjectId") REFERENCES iam.instance_subjects ("Id") ON DELETE RESTRICT);
        CREATE INDEX "IX_instance_credentials_SubjectId" ON iam.instance_credentials ("SubjectId");
        CREATE TABLE iam.enrollment_grants (
          "Id" uuid NOT NULL,
          "SoftwareId" uuid NOT NULL,
          "DeviceIds" uuid[] NOT NULL,
          "SecretHash" varchar(64) NOT NULL,
          "ExpiresAt" timestamp with time zone NOT NULL,
          "MaxInstances" integer NOT NULL,
          "UsedCount" integer NOT NULL,
          "RevokedAt" timestamp with time zone,
          "Revision" bigint NOT NULL,
          CONSTRAINT "PK_enrollment_grants" PRIMARY KEY ("Id"),
          CONSTRAINT "CK_enrollment_capacity" CHECK ("MaxInstances">0 AND "UsedCount">=0 AND "UsedCount"<="MaxInstances"));
        CREATE INDEX "IX_enrollment_grants_SoftwareId" ON iam.enrollment_grants ("SoftwareId");
        CREATE TABLE iam.registrations (
          "Id" uuid NOT NULL,
          "GrantId" uuid NOT NULL,
          "SoftwareId" uuid NOT NULL,
          "InstallationKey" uuid NOT NULL,
          "DeviceId" uuid NOT NULL,
          "Key" uuid NOT NULL,
          "RequestDigest" varchar(64) NOT NULL,
          "InstanceId" uuid NOT NULL,
          "CredentialId" uuid NOT NULL,
          CONSTRAINT "PK_registrations" PRIMARY KEY ("Id"),
          CONSTRAINT "FK_registrations_enrollment_grants_GrantId" FOREIGN KEY ("GrantId") REFERENCES iam.enrollment_grants ("Id") ON DELETE RESTRICT,
          CONSTRAINT "FK_registrations_instance_credentials_CredentialId" FOREIGN KEY ("CredentialId") REFERENCES iam.instance_credentials ("Id") ON DELETE RESTRICT);
        CREATE UNIQUE INDEX "IX_registrations_SoftwareId_InstallationKey" ON iam.registrations ("SoftwareId","InstallationKey");
        CREATE UNIQUE INDEX "IX_registrations_GrantId_Key" ON iam.registrations ("GrantId","Key");
        CREATE UNIQUE INDEX "IX_registrations_InstanceId" ON iam.registrations ("InstanceId");
        CREATE TABLE iam.recovery_grants (
          "Id" uuid NOT NULL,
          "SoftwareId" uuid NOT NULL,
          "InstanceId" uuid NOT NULL,
          "SecretHash" varchar(64) NOT NULL,
          "ExpiresAt" timestamp with time zone NOT NULL,
          "RevokedAt" timestamp with time zone,
          "Revision" bigint NOT NULL,
          "UsedKey" uuid,
          "RequestDigest" varchar(64),
          "CredentialId" uuid,
          "ResultEpoch" bigint,
          CONSTRAINT "PK_recovery_grants" PRIMARY KEY ("Id"),
          CONSTRAINT "FK_recovery_grants_instance_subjects_InstanceId" FOREIGN KEY ("InstanceId") REFERENCES iam.instance_subjects ("Id") ON DELETE RESTRICT);
        CREATE INDEX "IX_recovery_grants_InstanceId" ON iam.recovery_grants ("InstanceId");
        CREATE TABLE ins.instances (
          "Id" uuid NOT NULL,
          "SoftwareId" uuid NOT NULL,
          "DeviceId" uuid NOT NULL,
          "InstallationKey" uuid NOT NULL,
          "Lifecycle" varchar(32) NOT NULL,
          "Revision" bigint NOT NULL,
          CONSTRAINT "PK_instances" PRIMARY KEY ("Id"),
          CONSTRAINT "FK_instances_device_software" FOREIGN KEY ("DeviceId","SoftwareId") REFERENCES ins.device_software_bindings ("DeviceId","SoftwareId") ON DELETE RESTRICT);
        CREATE UNIQUE INDEX "IX_instances_SoftwareId_InstallationKey" ON ins.instances ("SoftwareId","InstallationKey");
        CREATE INDEX "IX_instances_DeviceId_SoftwareId_Id" ON ins.instances ("DeviceId","SoftwareId","Id");
        CREATE TABLE ins.instance_snapshots (
          "InstanceId" uuid NOT NULL,
          "SoftwareId" uuid NOT NULL,
          "StreamEpoch" bigint NOT NULL,
          "StreamOpen" boolean NOT NULL,
          "ReportSeq" bigint NOT NULL,
          "LastAcceptedAt" timestamp with time zone,
          "SnapshotJson" jsonb,
          "RequestDigest" varchar(64),
          "InstallationDigest" varchar(64),
          "EvidenceId" uuid,
          CONSTRAINT "PK_instance_snapshots" PRIMARY KEY ("InstanceId"),
          CONSTRAINT "FK_instance_snapshots_instances_InstanceId" FOREIGN KEY ("InstanceId") REFERENCES ins.instances ("Id") ON DELETE RESTRICT,
          CONSTRAINT "CK_report_sequence" CHECK ("StreamEpoch">=0 AND "ReportSeq">=0));
        CREATE INDEX "IX_instance_snapshots_SoftwareId_LastAcceptedAt_InstanceId" ON ins.instance_snapshots ("SoftwareId","LastAcceptedAt","InstanceId");
        CREATE TABLE ins.installation_evidence (
          "Id" uuid NOT NULL,
          "InstanceId" uuid NOT NULL,
          "SoftwareId" uuid NOT NULL,
          "StreamEpoch" bigint NOT NULL,
          "ReportSeq" bigint NOT NULL,
          "InstallationState" varchar(32) NOT NULL,
          "InstalledReleaseId" uuid,
          "InstalledVersion" varchar(128),
          "InstalledAt" timestamp with time zone,
          "ReceivedAt" timestamp with time zone NOT NULL,
          "ReportedRunningState" varchar(32) NOT NULL,
          "RequestDigest" varchar(64) NOT NULL,
          CONSTRAINT "PK_installation_evidence" PRIMARY KEY ("Id"),
          CONSTRAINT "FK_installation_evidence_instances_InstanceId" FOREIGN KEY ("InstanceId") REFERENCES ins.instances ("Id") ON DELETE RESTRICT);
        CREATE UNIQUE INDEX "IX_installation_evidence_InstanceId_StreamEpoch_ReportSeq" ON ins.installation_evidence ("InstanceId","StreamEpoch","ReportSeq");
        CREATE INDEX "IX_installation_evidence_InstanceId_ReceivedAt_Id" ON ins.installation_evidence ("InstanceId","ReceivedAt","Id");
        CREATE TABLE ins.report_stream_receipts (
          "OperationId" uuid NOT NULL,
          "InstanceId" uuid NOT NULL,
          "Epoch" bigint NOT NULL,
          CONSTRAINT "PK_report_stream_receipts" PRIMARY KEY ("OperationId"),
          CONSTRAINT "FK_report_stream_receipts_instances_InstanceId" FOREIGN KEY ("InstanceId") REFERENCES ins.instances ("Id") ON DELETE RESTRICT);
        ALTER TABLE iam.instance_subjects ADD CONSTRAINT "FK_instance_subjects_software" FOREIGN KEY ("SoftwareId") REFERENCES rel.software ("Id") ON DELETE RESTRICT;
        ALTER TABLE iam.enrollment_grants ADD CONSTRAINT "FK_enrollment_grants_software" FOREIGN KEY ("SoftwareId") REFERENCES rel.software ("Id") ON DELETE RESTRICT;
        ALTER TABLE iam.instance_subjects ADD CONSTRAINT "FK_instance_subjects_instance" FOREIGN KEY ("Id") REFERENCES ins.instances ("Id") ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED;
        CREATE INDEX "IX_registrations_CredentialId" ON iam.registrations ("CredentialId");
        CREATE INDEX "IX_report_stream_receipts_InstanceId" ON ins.report_stream_receipts ("InstanceId");
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Instance identities and installation history require an explicit recovery plan.");
    protected override void BuildTargetModel(ModelBuilder model)
    {
        SvmDbContextModelSnapshot.Configure(model); SvmDbContextModelSnapshot.ConfigurePersonnelV1(model);
        SvmDbContextModelSnapshot.ConfigureOperationResultsV1(model); BusOutboxV1Model.Configure(model); CatalogV1Model.Configure(model);
        InstanceAccessV1Model.Configure(model);
    }
}
