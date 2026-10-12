using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;

namespace Svm.EntityFrameworkCore.Migrations;

[DbContext(typeof(SvmDbContext))]
[Migration("20261010000100_ReleasesAndPackages")]
internal sealed class ReleasesAndPackagesMigration : Migration
{
    public ReleasesAndPackagesMigration() { }
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE rel.releases (
          "Id" uuid NOT NULL,
          "SoftwareId" uuid NOT NULL,
          "Major" integer NOT NULL,
          "Minor" integer NOT NULL,
          "Patch" integer NOT NULL,
          "State" varchar(32) NOT NULL,
          "ChangeLevel" varchar(16) NOT NULL,
          "ChangeSummary" varchar(2000) NOT NULL,
          "ChangeReason" varchar(256) NOT NULL,
          "PackageId" uuid NOT NULL,
          "CreatedBy" uuid NOT NULL,
          "CreatedAt" timestamptz NOT NULL,
          "DisabledAt" timestamptz,
          "DisableReason" varchar(256),
          "Revision" bigint NOT NULL,
          CONSTRAINT "PK_releases" PRIMARY KEY ("Id")
        );
        CREATE UNIQUE INDEX "IX_releases_SoftwareId_Major_Minor_Patch" ON rel.releases ("SoftwareId","Major","Minor","Patch");
        CREATE UNIQUE INDEX "IX_releases_PackageId" ON rel.releases ("PackageId");
        CREATE TABLE pkg.packages (
          "Id" uuid NOT NULL,
          "UploadId" uuid NOT NULL,
          "ReleaseId" uuid NOT NULL,
          "SoftwareId" uuid NOT NULL,
          "FileName" varchar(255) NOT NULL,
          "ExpectedSize" bigint NOT NULL,
          "ExpectedSha256" varchar(64) NOT NULL,
          "SizeBytes" bigint,
          "Sha256" varchar(64),
          "State" varchar(32) NOT NULL,
          "Disabled" boolean NOT NULL,
          "Revision" bigint NOT NULL,
          CONSTRAINT "PK_packages" PRIMARY KEY ("Id")
        );
        CREATE UNIQUE INDEX "IX_packages_ReleaseId" ON pkg.packages ("ReleaseId");
        CREATE UNIQUE INDEX "IX_packages_UploadId" ON pkg.packages ("UploadId");
        CREATE INDEX "IX_packages_State_Disabled" ON pkg.packages ("State","Disabled");
        CREATE TABLE pkg.works (
          "Id" uuid NOT NULL,
          "PackageId" uuid NOT NULL,
          "SoftwareId" uuid NOT NULL,
          "InitiatorId" uuid NOT NULL,
          "Kind" varchar(32) NOT NULL,
          "State" varchar(32) NOT NULL,
          "Stage" varchar(32) NOT NULL,
          "SourceNode" varchar(64) NOT NULL,
          "ReceiveToken" uuid,
          "ReceiveGeneration" bigint NOT NULL,
          "DispatchSequence" bigint NOT NULL,
          "DispatchEventId" uuid NOT NULL,
          "Accepted" boolean NOT NULL,
          "LeaseToken" uuid,
          "LeaseGeneration" bigint NOT NULL,
          "LeaseNode" varchar(64),
          "LeaseUntil" timestamptz,
          "CreatedAt" timestamptz NOT NULL,
          "CompletedAt" timestamptz,
          "LastErrorCode" varchar(64),
          "Revision" bigint NOT NULL,
          CONSTRAINT "PK_works" PRIMARY KEY ("Id")
        );
        CREATE INDEX "IX_works_PackageId_CreatedAt" ON pkg.works ("PackageId","CreatedAt");
        CREATE INDEX "IX_works_Accepted_State_LeaseUntil" ON pkg.works ("Accepted","State","LeaseUntil");
        CREATE TABLE pkg.replicas (
          "Id" uuid NOT NULL,
          "PackageId" uuid NOT NULL,
          "NodeId" varchar(64) NOT NULL,
          "State" varchar(32) NOT NULL,
          "CheckedAt" timestamptz,
          CONSTRAINT "PK_replicas" PRIMARY KEY ("Id")
        );
        CREATE UNIQUE INDEX "IX_replicas_PackageId_NodeId" ON pkg.replicas ("PackageId","NodeId");
        CREATE TABLE pkg.download_sessions (
          "Id" uuid NOT NULL,
          "PackageId" uuid NOT NULL,
          "NodeId" varchar(64) NOT NULL,
          "WorkerGeneration" uuid NOT NULL,
          "SubjectId" uuid NOT NULL,
          "ActorKind" varchar(32) NOT NULL,
          "EmployeeNo" varchar(64),
          "StartedAt" timestamptz NOT NULL,
          "EndedAt" timestamptz,
          "BytesSent" bigint,
          "State" varchar(32) NOT NULL,
          CONSTRAINT "PK_download_sessions" PRIMARY KEY ("Id")
        );
        CREATE INDEX "IX_download_sessions_PackageId_State" ON pkg.download_sessions ("PackageId","State");
        CREATE INDEX "IX_download_sessions_NodeId_WorkerGeneration_State" ON pkg.download_sessions ("NodeId","WorkerGeneration","State");
        CREATE TABLE pkg.dispatches (
          "Id" uuid NOT NULL,
          "WorkId" uuid NOT NULL,
          "Sequence" bigint NOT NULL,
          CONSTRAINT "PK_dispatches" PRIMARY KEY ("Id")
        );
        CREATE UNIQUE INDEX "IX_dispatches_WorkId_Sequence" ON pkg.dispatches ("WorkId","Sequence");
        CREATE TABLE pkg.receive_attempts (
          "Id" uuid NOT NULL,
          "UploadId" uuid NOT NULL,
          "PackageId" uuid NOT NULL,
          "Generation" bigint NOT NULL,
          "NodeId" varchar(64) NOT NULL,
          CONSTRAINT "PK_receive_attempts" PRIMARY KEY ("Id")
        );
        CREATE UNIQUE INDEX "IX_receive_attempts_UploadId_Generation" ON pkg.receive_attempts ("UploadId","Generation");
        ALTER TABLE rel.releases ADD CONSTRAINT "FK_releases_SoftwareId" FOREIGN KEY ("SoftwareId") REFERENCES rel.software ("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE rel.releases ADD CONSTRAINT "FK_releases_PackageId" FOREIGN KEY ("PackageId") REFERENCES pkg.packages ("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE pkg.packages ADD CONSTRAINT "FK_packages_ReleaseId" FOREIGN KEY ("ReleaseId") REFERENCES rel.releases ("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE pkg.packages ADD CONSTRAINT "FK_packages_SoftwareId" FOREIGN KEY ("SoftwareId") REFERENCES rel.software ("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE pkg.works ADD CONSTRAINT "FK_works_PackageId" FOREIGN KEY ("PackageId") REFERENCES pkg.packages ("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE pkg.replicas ADD CONSTRAINT "FK_replicas_PackageId" FOREIGN KEY ("PackageId") REFERENCES pkg.packages ("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE pkg.download_sessions ADD CONSTRAINT "FK_download_sessions_PackageId" FOREIGN KEY ("PackageId") REFERENCES pkg.packages ("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE pkg.dispatches ADD CONSTRAINT "FK_dispatches_WorkId" FOREIGN KEY ("WorkId") REFERENCES pkg.works ("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE pkg.receive_attempts ADD CONSTRAINT "FK_receive_attempts_UploadId" FOREIGN KEY ("UploadId") REFERENCES pkg.works ("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE pkg.receive_attempts ADD CONSTRAINT "FK_receive_attempts_PackageId" FOREIGN KEY ("PackageId") REFERENCES pkg.packages ("Id") ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE rel.releases ADD CONSTRAINT "CK_release_number" CHECK ("Major">=1 AND "Minor">=0 AND "Patch">=0);
        ALTER TABLE pkg.packages ADD CONSTRAINT "CK_package_size" CHECK ("ExpectedSize">0 AND ("SizeBytes" IS NULL OR "SizeBytes"="ExpectedSize"));
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Releases, packages and download history must be retained.");
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        SvmDbContextModelSnapshot.Configure(modelBuilder);
        SvmDbContextModelSnapshot.ConfigurePersonnelV1(modelBuilder);
        SvmDbContextModelSnapshot.ConfigureOperationResultsV1(modelBuilder);
        BusOutboxV1Model.Configure(modelBuilder);
        CatalogV1Model.Configure(modelBuilder);
        InstanceAccessV1Model.Configure(modelBuilder);
        ReleasePackagesV1Model.Configure(modelBuilder);
    }
}
