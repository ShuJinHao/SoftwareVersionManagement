using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Svm.EntityFrameworkCore.Framework;

namespace Svm.EntityFrameworkCore.Migrations;

[DbContext(typeof(SvmDbContext))]
[Migration("20261012000100_TaskWorkflow")]
internal sealed class TaskWorkflowMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE tsk.target_selections (
          "Id" uuid NOT NULL,
          "SoftwareId" uuid NOT NULL,
          "OwnerId" uuid NOT NULL,
          "Mode" text NOT NULL,
          "Filter" text,
          "State" text NOT NULL,
          "MemberCount" integer NOT NULL,
          "ReceivedChunkCount" integer NOT NULL,
          "SnapshotAt" timestamp with time zone,
          "Revision" bigint NOT NULL,
          PRIMARY KEY ("Id")
        );
        CREATE INDEX "IX_target_selections_SoftwareId_OwnerId_Id" ON tsk.target_selections ("SoftwareId", "OwnerId", "Id");
        CREATE TABLE tsk.target_members (
          "SelectionId" uuid NOT NULL,
          "InstanceId" uuid NOT NULL,
          PRIMARY KEY ("SelectionId","InstanceId"),
          FOREIGN KEY ("SelectionId") REFERENCES tsk.target_selections ("Id") ON DELETE RESTRICT
        );
        CREATE TABLE tsk.selection_chunks (
          "SelectionId" uuid NOT NULL,
          "Number" integer NOT NULL,
          "Digest" text NOT NULL,
          "MemberCount" integer NOT NULL,
          "ReceivedChunkCount" integer NOT NULL,
          "Revision" bigint NOT NULL,
          PRIMARY KEY ("SelectionId","Number"),
          FOREIGN KEY ("SelectionId") REFERENCES tsk.target_selections ("Id") ON DELETE RESTRICT
        );
        CREATE TABLE tsk.deployments (
          "Id" uuid NOT NULL,
          "SoftwareId" uuid NOT NULL,
          "SelectionId" uuid NOT NULL,
          "TargetReleaseId" uuid NOT NULL,
          "CreatedAt" timestamp with time zone NOT NULL,
          "IsPrepared" boolean NOT NULL,
          "CreatedBy" uuid NOT NULL,
          "AuthorizationSubjectId" uuid NOT NULL,
          "RetryOfDeploymentId" uuid,
          "Reason" text NOT NULL,
          "State" text NOT NULL,
          "NotBefore" timestamp with time zone NOT NULL,
          "LatestStart" timestamp with time zone NOT NULL,
          "BatchSize" integer NOT NULL,
          "FailureLimit" integer NOT NULL,
          "ResultWaitSeconds" integer NOT NULL,
          "SelectedCount" integer NOT NULL,
          "ProcessedCount" integer NOT NULL,
          "AcceptedCount" integer NOT NULL,
          "RejectedCount" integer NOT NULL,
          "PauseCodes" text NOT NULL,
          "ControlPending" boolean NOT NULL,
          "Revision" bigint NOT NULL,
          PRIMARY KEY ("Id"),
          FOREIGN KEY ("SelectionId") REFERENCES tsk.target_selections ("Id") ON DELETE RESTRICT
        );
        CREATE INDEX "IX_deployments_SoftwareId_Id" ON tsk.deployments ("SoftwareId", "Id");
        CREATE TABLE tsk.admission_items (
          "DeploymentId" uuid NOT NULL,
          "InstanceId" uuid NOT NULL,
          "Decision" text NOT NULL,
          "ReasonCode" text,
          "TaskId" uuid,
          PRIMARY KEY ("DeploymentId","InstanceId"),
          FOREIGN KEY ("DeploymentId") REFERENCES tsk.deployments ("Id") ON DELETE RESTRICT
        );
        CREATE TABLE tsk.batches (
          "Id" uuid NOT NULL,
          "DeploymentId" uuid NOT NULL,
          "Ordinal" integer NOT NULL,
          "State" text NOT NULL,
          "OpenedAt" timestamp with time zone,
          "TaskCount" integer NOT NULL,
          "FailureRevision" bigint NOT NULL,
          "ReviewedFailureRevision" bigint NOT NULL,
          "Revision" bigint NOT NULL,
          PRIMARY KEY ("Id"),
          FOREIGN KEY ("DeploymentId") REFERENCES tsk.deployments ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_batches_DeploymentId_Ordinal" ON tsk.batches ("DeploymentId", "Ordinal");
        CREATE UNIQUE INDEX "IX_batches_DeploymentId" ON tsk.batches ("DeploymentId") WHERE "State"='Open';
        CREATE TABLE tsk.tasks (
          "Id" uuid NOT NULL,
          "DeploymentId" uuid NOT NULL,
          "InstanceId" uuid NOT NULL,
          "TargetReleaseId" uuid NOT NULL,
          "CreatedAt" timestamp with time zone NOT NULL,
          "BatchId" uuid,
          "State" text NOT NULL,
          "NotBefore" timestamp with time zone NOT NULL,
          "LatestStart" timestamp with time zone NOT NULL,
          "IsDeferred" boolean NOT NULL,
          "AttemptId" uuid,
          "ResponseDeadlineAt" timestamp with time zone,
          "LastReportedProgress" text,
          "LastProgressSequence" bigint NOT NULL,
          "TerminalResult" text,
          "RetryOfTaskId" uuid,
          "ClosedBy" uuid,
          "ClosureReason" text,
          "OnsiteEvidence" text,
          "ClosedAt" timestamp with time zone,
          "Revision" bigint NOT NULL,
          PRIMARY KEY ("Id"),
          FOREIGN KEY ("DeploymentId") REFERENCES tsk.deployments ("Id") ON DELETE RESTRICT,
          FOREIGN KEY ("BatchId") REFERENCES tsk.batches ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_tasks_DeploymentId_InstanceId" ON tsk.tasks ("DeploymentId", "InstanceId");
        CREATE INDEX "IX_tasks_InstanceId_Id" ON tsk.tasks ("InstanceId", "Id");
        CREATE INDEX "IX_tasks_BatchId_State" ON tsk.tasks ("BatchId", "State");
        CREATE UNIQUE INDEX "IX_tasks_InstanceId" ON tsk.tasks ("InstanceId") WHERE "State" NOT IN ('Succeeded','Failed','Canceled','ClosedUnknown');
        CREATE TABLE tsk.attempts (
          "Id" uuid NOT NULL,
          "TaskId" uuid NOT NULL,
          "PackageId" uuid,
          "Sha256" text,
          "StartAuthorizedAt" timestamp with time zone,
          "MustBeginBefore" timestamp with time zone,
          PRIMARY KEY ("Id"),
          FOREIGN KEY ("TaskId") REFERENCES tsk.tasks ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_attempts_TaskId" ON tsk.attempts ("TaskId");
        CREATE TABLE tsk.receipts (
          "Id" uuid NOT NULL,
          "AttemptId" uuid NOT NULL,
          "EventId" uuid NOT NULL,
          "Sequence" bigint NOT NULL,
          "Kind" text NOT NULL,
          "Progress" text,
          "Result" text,
          "FailureCode" text,
          "Detail" text,
          "Digest" text NOT NULL,
          "ReceivedAt" timestamp with time zone NOT NULL,
          "Applied" boolean NOT NULL,
          "LateAfterClosure" boolean NOT NULL,
          "TaskState" text NOT NULL,
          "StateReportApplied" boolean,
          "StateReportJson" text,
          PRIMARY KEY ("Id"),
          FOREIGN KEY ("AttemptId") REFERENCES tsk.attempts ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_receipts_AttemptId_EventId" ON tsk.receipts ("AttemptId", "EventId");
        CREATE UNIQUE INDEX "IX_receipts_AttemptId_Sequence" ON tsk.receipts ("AttemptId", "Sequence");
        CREATE TABLE tsk.instance_execution_guards (
          "InstanceId" uuid NOT NULL,
          "TaskId" uuid NOT NULL,
          PRIMARY KEY ("InstanceId"),
          FOREIGN KEY ("TaskId") REFERENCES tsk.tasks ("Id") ON DELETE RESTRICT
        );
        CREATE TABLE tsk.works (
          "Id" uuid NOT NULL,
          "SoftwareId" uuid NOT NULL,
          "ResourceId" uuid NOT NULL,
          "InitiatorId" uuid NOT NULL,
          "Kind" text NOT NULL,
          "State" text NOT NULL,
          "Stage" text NOT NULL,
          "Accepted" boolean NOT NULL,
          "DispatchSequence" bigint NOT NULL,
          "DispatchEventId" uuid NOT NULL,
          "LeaseToken" uuid,
          "LeaseGeneration" bigint NOT NULL,
          "LeaseUntil" timestamp with time zone,
          "Cursor" uuid,
          "ProcessedItems" integer NOT NULL,
          "LastErrorCode" text,
          "NewNotBefore" timestamp with time zone,
          "NewLatestStart" timestamp with time zone,
          "NextScanAt" timestamp with time zone NOT NULL,
          "Revision" bigint NOT NULL,
          PRIMARY KEY ("Id")
        );
        CREATE INDEX "IX_works_Accepted_State_NextScanAt_LeaseUntil_Id" ON tsk.works ("Accepted", "State", "NextScanAt", "LeaseUntil", "Id");
        CREATE TABLE tsk.dispatches (
          "Id" uuid NOT NULL,
          "WorkId" uuid NOT NULL,
          "Sequence" bigint NOT NULL,
          "Kind" text NOT NULL,
          PRIMARY KEY ("Id"),
          FOREIGN KEY ("WorkId") REFERENCES tsk.works ("Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX "IX_task_dispatches_WorkId_Sequence" ON tsk.dispatches ("WorkId", "Sequence");
        CREATE TABLE tsk.control_items (
          "WorkId" uuid NOT NULL,
          "TaskId" uuid NOT NULL,
          "Outcome" text NOT NULL,
          "ReasonCode" text,
          PRIMARY KEY ("WorkId","TaskId"),
          FOREIGN KEY ("WorkId") REFERENCES tsk.works ("Id") ON DELETE RESTRICT,
          FOREIGN KEY ("TaskId") REFERENCES tsk.tasks ("Id") ON DELETE RESTRICT
        );
        CREATE INDEX "IX_deployments_SelectionId" ON tsk.deployments ("SelectionId");
        CREATE INDEX "IX_instance_execution_guards_TaskId" ON tsk.instance_execution_guards ("TaskId");
        CREATE INDEX "IX_control_items_TaskId" ON tsk.control_items ("TaskId");
        CREATE TABLE rel.integration_materials (
         "Id" uuid PRIMARY KEY, "ReleaseId" uuid NOT NULL REFERENCES rel.releases("Id") ON DELETE RESTRICT,
         "Revision" bigint NOT NULL, "DataLocations" text[] NOT NULL, "UpdateBehavior" text NOT NULL,
         "RollbackBehavior" text NOT NULL, "RecoveryPlan" text NOT NULL, "VerificationConclusion" text,
         "EvidenceReferences" text[] NOT NULL, "Reason" text NOT NULL, "RecordedBy" uuid NOT NULL REFERENCES iam.users("Id") ON DELETE RESTRICT,
         "RecordedAt" timestamp with time zone NOT NULL);
        CREATE UNIQUE INDEX "IX_integration_materials_ReleaseId_Revision" ON rel.integration_materials("ReleaseId","Revision");
        ALTER TABLE tsk.target_selections ADD CONSTRAINT "FK_selection_software" FOREIGN KEY ("SoftwareId") REFERENCES rel.software("Id") ON DELETE RESTRICT;
        ALTER TABLE tsk.target_selections ADD CONSTRAINT "FK_selection_person" FOREIGN KEY ("OwnerId") REFERENCES iam.users("Id") ON DELETE RESTRICT;
        ALTER TABLE tsk.deployments ADD CONSTRAINT "FK_deployment_creator" FOREIGN KEY ("CreatedBy") REFERENCES iam.users("Id") ON DELETE RESTRICT;
        ALTER TABLE tsk.deployments ADD CONSTRAINT "FK_deployment_authority" FOREIGN KEY ("AuthorizationSubjectId") REFERENCES iam.users("Id") ON DELETE RESTRICT;
        ALTER TABLE tsk.tasks ADD CONSTRAINT "FK_task_attempt" FOREIGN KEY ("AttemptId") REFERENCES tsk.attempts("Id") ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED;
        ALTER TABLE tsk.deployments ADD CONSTRAINT "FK_deployment_release" FOREIGN KEY ("TargetReleaseId") REFERENCES rel.releases("Id") ON DELETE RESTRICT;
        ALTER TABLE tsk.tasks ADD CONSTRAINT "FK_task_instance" FOREIGN KEY ("InstanceId") REFERENCES ins.instances("Id") ON DELETE RESTRICT;
        ALTER TABLE tsk.tasks ADD CONSTRAINT "FK_task_release" FOREIGN KEY ("TargetReleaseId") REFERENCES rel.releases("Id") ON DELETE RESTRICT;
        ALTER TABLE tsk.tasks ADD CONSTRAINT "FK_task_retry" FOREIGN KEY ("RetryOfTaskId") REFERENCES tsk.tasks("Id") ON DELETE RESTRICT;
        ALTER TABLE tsk.deployments ADD CONSTRAINT "FK_deployment_retry" FOREIGN KEY ("RetryOfDeploymentId") REFERENCES tsk.deployments("Id") ON DELETE RESTRICT;
        ALTER TABLE tsk.attempts ADD CONSTRAINT "FK_attempt_package" FOREIGN KEY ("PackageId") REFERENCES pkg.packages("Id") ON DELETE RESTRICT;
        ALTER TABLE tsk.tasks ADD CONSTRAINT "CK_task_terminal" CHECK (("State" IN ('Succeeded','Failed','Canceled','ClosedUnknown'))=("TerminalResult" IS NOT NULL));
        ALTER TABLE tsk.receipts ADD CONSTRAINT "CK_receipt_sequence" CHECK ("Sequence">0);
        ALTER TABLE tsk.deployments ADD CONSTRAINT "CK_deployment_window" CHECK ("NotBefore"<"LatestStart" AND "BatchSize">0 AND "FailureLimit">0 AND "ResultWaitSeconds">0);
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Task and receipt history must be retained.");
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        SvmDbContextModelSnapshot.Configure(modelBuilder);
        SvmDbContextModelSnapshot.ConfigurePersonnelV1(modelBuilder);
        SvmDbContextModelSnapshot.ConfigureOperationResultsV1(modelBuilder);
        BusOutboxV1Model.Configure(modelBuilder);
        CatalogV1Model.Configure(modelBuilder);
        InstanceAccessV1Model.Configure(modelBuilder);
        ReleasePackagesV1Model.Configure(modelBuilder);
        ReleasePublicationV1Model.Configure(modelBuilder);
        TaskWorkflowV1Model.Configure(modelBuilder);
        IntegrationMaterialsV1Model.Configure(modelBuilder);
    }
}
