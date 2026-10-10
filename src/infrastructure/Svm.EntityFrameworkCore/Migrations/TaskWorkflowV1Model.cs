using Microsoft.EntityFrameworkCore;

namespace Svm.EntityFrameworkCore.Migrations;

// Frozen property model for this migration. Do not delegate to live module mappings.
internal static class TaskWorkflowV1Model
{
    internal static void Configure(ModelBuilder m)
    {
        m.Entity("Svm.Core.Tasks.TargetSelection", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("SoftwareId");
            b.Property<Guid>("OwnerId");
            b.Property<string>("Mode").IsRequired();
            b.Property<string?>("Filter");
            b.Property<string>("State").IsRequired();
            b.Property<int>("MemberCount");
            b.Property<int>("ReceivedChunkCount");
            b.Property<DateTimeOffset?>("SnapshotAt");
            b.Property<long>("Revision");
            b.HasKey("Id"); b.ToTable("target_selections", "tsk");
            b.HasIndex("SoftwareId", "OwnerId", "Id");
        });
        m.Entity("Svm.Core.Tasks.TargetMember", b =>
        {
            b.Property<Guid>("SelectionId").ValueGeneratedNever();
            b.Property<Guid>("InstanceId").ValueGeneratedNever();
            b.HasKey("SelectionId", "InstanceId"); b.ToTable("target_members", "tsk");
            b.HasOne("Svm.Core.Tasks.TargetSelection", null).WithMany().HasForeignKey("SelectionId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        m.Entity("Svm.Core.Tasks.SelectionChunk", b =>
        {
            b.Property<Guid>("SelectionId").ValueGeneratedNever();
            b.Property<int>("Number");
            b.Property<string>("Digest").IsRequired();
            b.Property<int>("MemberCount");
            b.Property<int>("ReceivedChunkCount");
            b.Property<long>("Revision");
            b.HasKey("SelectionId", "Number"); b.ToTable("selection_chunks", "tsk");
            b.HasOne("Svm.Core.Tasks.TargetSelection", null).WithMany().HasForeignKey("SelectionId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        m.Entity("Svm.Core.Tasks.Deployment", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("SoftwareId");
            b.Property<Guid>("SelectionId");
            b.Property<Guid>("TargetReleaseId");
            b.Property<DateTimeOffset>("CreatedAt");
            b.Property<bool>("IsPrepared");
            b.Property<Guid>("CreatedBy");
            b.Property<Guid>("AuthorizationSubjectId");
            b.Property<Guid?>("RetryOfDeploymentId");
            b.Property<string>("Reason").IsRequired();
            b.Property<string>("State").IsRequired();
            b.Property<DateTimeOffset>("NotBefore");
            b.Property<DateTimeOffset>("LatestStart");
            b.Property<int>("BatchSize");
            b.Property<int>("FailureLimit");
            b.Property<int>("ResultWaitSeconds");
            b.Property<int>("SelectedCount");
            b.Property<int>("ProcessedCount");
            b.Property<int>("AcceptedCount");
            b.Property<int>("RejectedCount");
            b.Property<string>("PauseCodes").IsRequired();
            b.Property<bool>("ControlPending");
            b.Property<long>("Revision");
            b.HasKey("Id"); b.ToTable("deployments", "tsk");
            b.HasOne("Svm.Core.Tasks.TargetSelection", null).WithMany().HasForeignKey("SelectionId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasIndex("SoftwareId", "Id");
            b.HasIndex("SelectionId");
        });
        m.Entity("Svm.Core.Tasks.AdmissionItem", b =>
        {
            b.Property<Guid>("DeploymentId").ValueGeneratedNever();
            b.Property<Guid>("InstanceId").ValueGeneratedNever();
            b.Property<string>("Decision").IsRequired();
            b.Property<string?>("ReasonCode");
            b.Property<Guid?>("TaskId");
            b.HasKey("DeploymentId", "InstanceId"); b.ToTable("admission_items", "tsk");
            b.HasOne("Svm.Core.Tasks.Deployment", null).WithMany().HasForeignKey("DeploymentId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        m.Entity("Svm.Core.Tasks.TaskBatch", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("DeploymentId");
            b.Property<int>("Ordinal");
            b.Property<string>("State").IsRequired();
            b.Property<DateTimeOffset?>("OpenedAt");
            b.Property<int>("TaskCount");
            b.Property<long>("FailureRevision");
            b.Property<long>("ReviewedFailureRevision");
            b.Property<long>("Revision");
            b.HasKey("Id"); b.ToTable("batches", "tsk");
            b.HasOne("Svm.Core.Tasks.Deployment", null).WithMany().HasForeignKey("DeploymentId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasIndex("DeploymentId", "Ordinal").IsUnique();
            b.HasIndex("DeploymentId").IsUnique().HasFilter("\"State\"='Open'");
        });
        m.Entity("Svm.Core.Tasks.InstanceTask", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("DeploymentId");
            b.Property<Guid>("InstanceId");
            b.Property<Guid>("TargetReleaseId");
            b.Property<DateTimeOffset>("CreatedAt");
            b.Property<Guid?>("BatchId");
            b.Property<string>("State").IsRequired();
            b.Property<DateTimeOffset>("NotBefore");
            b.Property<DateTimeOffset>("LatestStart");
            b.Property<bool>("IsDeferred");
            b.Property<Guid?>("AttemptId");
            b.Property<DateTimeOffset?>("ResponseDeadlineAt");
            b.Property<string?>("LastReportedProgress");
            b.Property<long>("LastProgressSequence");
            b.Property<string?>("TerminalResult");
            b.Property<Guid?>("RetryOfTaskId");
            b.Property<Guid?>("ClosedBy");
            b.Property<string?>("ClosureReason");
            b.Property<string?>("OnsiteEvidence");
            b.Property<DateTimeOffset?>("ClosedAt");
            b.Property<long>("Revision");
            b.HasKey("Id"); b.ToTable("tasks", "tsk");
            b.HasOne("Svm.Core.Tasks.Deployment", null).WithMany().HasForeignKey("DeploymentId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Svm.Core.Tasks.TaskBatch", null).WithMany().HasForeignKey("BatchId").OnDelete(DeleteBehavior.Restrict);
            b.HasIndex("DeploymentId", "InstanceId").IsUnique();
            b.HasIndex("InstanceId", "Id");
            b.HasIndex("BatchId", "State");
            b.HasIndex("InstanceId").IsUnique().HasFilter("\"State\" NOT IN ('Succeeded','Failed','Canceled','ClosedUnknown')");
        });
        m.Entity("Svm.Core.Tasks.ExecutionAttempt", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("TaskId");
            b.Property<Guid?>("PackageId");
            b.Property<string?>("Sha256");
            b.Property<DateTimeOffset?>("StartAuthorizedAt");
            b.Property<DateTimeOffset?>("MustBeginBefore");
            b.HasKey("Id"); b.ToTable("attempts", "tsk");
            b.HasOne("Svm.Core.Tasks.InstanceTask", null).WithMany().HasForeignKey("TaskId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasIndex("TaskId").IsUnique();
        });
        m.Entity("Svm.Core.Tasks.TaskReceipt", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("AttemptId");
            b.Property<Guid>("EventId");
            b.Property<long>("Sequence");
            b.Property<string>("Kind").IsRequired();
            b.Property<string?>("Progress");
            b.Property<string?>("Result");
            b.Property<string?>("FailureCode");
            b.Property<string?>("Detail");
            b.Property<string>("Digest").IsRequired();
            b.Property<DateTimeOffset>("ReceivedAt");
            b.Property<bool>("Applied");
            b.Property<bool>("LateAfterClosure");
            b.Property<string>("TaskState").IsRequired();
            b.Property<bool?>("StateReportApplied");
            b.Property<string?>("StateReportJson");
            b.HasKey("Id"); b.ToTable("receipts", "tsk");
            b.HasOne("Svm.Core.Tasks.ExecutionAttempt", null).WithMany().HasForeignKey("AttemptId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasIndex("AttemptId", "EventId").IsUnique();
            b.HasIndex("AttemptId", "Sequence").IsUnique();
        });
        m.Entity("Svm.Core.Tasks.InstanceExecutionGuard", b =>
        {
            b.Property<Guid>("InstanceId").ValueGeneratedNever();
            b.Property<Guid>("TaskId");
            b.HasKey("InstanceId"); b.ToTable("instance_execution_guards", "tsk");
            b.HasIndex("TaskId");
            b.HasOne("Svm.Core.Tasks.InstanceTask", null).WithMany().HasForeignKey("TaskId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        m.Entity("Svm.Core.Tasks.TaskWork", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("SoftwareId");
            b.Property<Guid>("ResourceId");
            b.Property<Guid>("InitiatorId");
            b.Property<string>("Kind").IsRequired();
            b.Property<string>("State").IsRequired();
            b.Property<string>("Stage").IsRequired();
            b.Property<bool>("Accepted");
            b.Property<long>("DispatchSequence");
            b.Property<Guid>("DispatchEventId");
            b.Property<Guid?>("LeaseToken");
            b.Property<long>("LeaseGeneration");
            b.Property<DateTimeOffset?>("LeaseUntil");
            b.Property<Guid?>("Cursor");
            b.Property<int>("ProcessedItems");
            b.Property<string?>("LastErrorCode");
            b.Property<DateTimeOffset?>("NewNotBefore");
            b.Property<DateTimeOffset?>("NewLatestStart");
            b.Property<DateTimeOffset>("NextScanAt");
            b.Property<long>("Revision");
            b.HasKey("Id"); b.ToTable("works", "tsk");
            b.HasIndex("Accepted", "State", "NextScanAt", "LeaseUntil", "Id");
        });
        m.Entity("Svm.Core.Tasks.TaskDispatch", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever();
            b.Property<Guid>("WorkId");
            b.Property<long>("Sequence");
            b.Property<string>("Kind").IsRequired();
            b.HasKey("Id"); b.ToTable("dispatches", "tsk");
            b.HasOne("Svm.Core.Tasks.TaskWork", null).WithMany().HasForeignKey("WorkId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasIndex("WorkId", "Sequence").IsUnique().HasDatabaseName("IX_task_dispatches_WorkId_Sequence");
        });
        m.Entity("Svm.Core.Tasks.TaskControlItem", b =>
        {
            b.Property<Guid>("WorkId").ValueGeneratedNever();
            b.Property<Guid>("TaskId").ValueGeneratedNever();
            b.Property<string>("Outcome").IsRequired();
            b.Property<string?>("ReasonCode");
            b.HasKey("WorkId", "TaskId"); b.ToTable("control_items", "tsk");
            b.HasIndex("TaskId");
            b.HasOne("Svm.Core.Tasks.TaskWork", null).WithMany().HasForeignKey("WorkId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Svm.Core.Tasks.InstanceTask", null).WithMany().HasForeignKey("TaskId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
    }
}
