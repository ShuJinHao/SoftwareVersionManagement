using Microsoft.EntityFrameworkCore;
using Svm.Core.Tasks;
using Svm.SharedKernel.Domain;

namespace Svm.EntityFrameworkCore.Tasks;

internal static class TaskModel
{
    internal static void Configure(ModelBuilder m)
    {
        m.Entity<TargetSelection>(b =>
        {
            b.ToTable("target_selections", "tsk");
            b.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<TargetSelection>(x)); b.Ignore(x => x.DomainEvents);
            b.HasKey("Id");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex("SoftwareId", "OwnerId", "Id");
        });
        m.Entity<TargetMember>(b =>
        {
            b.ToTable("target_members", "tsk");
            b.HasKey("SelectionId", "InstanceId");
            b.Property(x => x.SelectionId).HasConversion(x => x.Value, x => new StrongId<TargetSelection>(x));
            b.Property(x => x.SelectionId).ValueGeneratedNever();
            b.Property(x => x.InstanceId).ValueGeneratedNever();
            b.HasOne<TargetSelection>().WithMany().HasForeignKey("SelectionId").OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<SelectionChunk>(b =>
        {
            b.ToTable("selection_chunks", "tsk");
            b.HasKey("SelectionId", "Number");
            b.Property(x => x.SelectionId).HasConversion(x => x.Value, x => new StrongId<TargetSelection>(x));
            b.Property(x => x.SelectionId).ValueGeneratedNever();
            b.HasOne<TargetSelection>().WithMany().HasForeignKey("SelectionId").OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Deployment>(b =>
        {
            b.ToTable("deployments", "tsk");
            b.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<Deployment>(x)); b.Ignore(x => x.DomainEvents);
            b.Ignore(x => x.Ended);
            b.HasKey("Id");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.SelectionId).HasConversion(x => x.Value, x => new StrongId<TargetSelection>(x));
            b.HasOne<TargetSelection>().WithMany().HasForeignKey("SelectionId").OnDelete(DeleteBehavior.Restrict);
            b.HasIndex("SoftwareId", "Id");
        });
        m.Entity<AdmissionItem>(b =>
        {
            b.ToTable("admission_items", "tsk");
            b.HasKey("DeploymentId", "InstanceId");
            b.Property(x => x.DeploymentId).HasConversion(x => x.Value, x => new StrongId<Deployment>(x));
            b.Property(x => x.DeploymentId).ValueGeneratedNever();
            b.Property(x => x.InstanceId).ValueGeneratedNever();
            b.HasOne<Deployment>().WithMany().HasForeignKey("DeploymentId").OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<TaskBatch>(b =>
        {
            b.ToTable("batches", "tsk");
            b.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<TaskBatch>(x)); b.Ignore(x => x.DomainEvents);
            b.HasKey("Id");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.DeploymentId).HasConversion(x => x.Value, x => new StrongId<Deployment>(x));
            b.HasOne<Deployment>().WithMany().HasForeignKey("DeploymentId").OnDelete(DeleteBehavior.Restrict);
            b.HasIndex("DeploymentId", "Ordinal").IsUnique();
            b.HasIndex("DeploymentId").IsUnique().HasFilter("\"State\"='Open'");
        });
        m.Entity<InstanceTask>(b =>
        {
            b.ToTable("tasks", "tsk");
            b.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<InstanceTask>(x)); b.Ignore(x => x.DomainEvents);
            b.Ignore(x => x.Ended);
            b.HasKey("Id");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.DeploymentId).HasConversion(x => x.Value, x => new StrongId<Deployment>(x));
            b.Property(x => x.BatchId).HasConversion(x => x.HasValue ? x.Value.Value : (Guid?)null, x => x.HasValue ? new StrongId<TaskBatch>(x.Value) : (StrongId<TaskBatch>?)null);
            b.HasOne<Deployment>().WithMany().HasForeignKey("DeploymentId").OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TaskBatch>().WithMany().HasForeignKey("BatchId").OnDelete(DeleteBehavior.Restrict);
            b.HasIndex("DeploymentId", "InstanceId").IsUnique();
            b.HasIndex("InstanceId", "Id");
            b.HasIndex("BatchId", "State");
            b.HasIndex("InstanceId").IsUnique().HasFilter("\"State\" NOT IN ('Succeeded','Failed','Canceled','ClosedUnknown')");
        });
        m.Entity<ExecutionAttempt>(b =>
        {
            b.ToTable("attempts", "tsk");
            b.HasKey("Id");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.TaskId).HasConversion(x => x.Value, x => new StrongId<InstanceTask>(x));
            b.HasOne<InstanceTask>().WithMany().HasForeignKey("TaskId").OnDelete(DeleteBehavior.Restrict);
            b.HasIndex("TaskId").IsUnique();
        });
        m.Entity<TaskReceipt>(b =>
        {
            b.ToTable("receipts", "tsk");
            b.HasKey("Id");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasOne<ExecutionAttempt>().WithMany().HasForeignKey("AttemptId").OnDelete(DeleteBehavior.Restrict);
            b.HasIndex("AttemptId", "EventId").IsUnique();
            b.HasIndex("AttemptId", "Sequence").IsUnique();
        });
        m.Entity<InstanceExecutionGuard>(b =>
        {
            b.ToTable("instance_execution_guards", "tsk");
            b.HasKey("InstanceId");
            b.Property(x => x.InstanceId).ValueGeneratedNever();
            b.Property(x => x.TaskId).HasConversion(x => x.Value, x => new StrongId<InstanceTask>(x));
            b.HasOne<InstanceTask>().WithMany().HasForeignKey("TaskId").OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<TaskWork>(b =>
        {
            b.ToTable("works", "tsk");
            b.Property(x => x.Id).HasConversion(x => x.Value, x => new StrongId<TaskWork>(x)); b.Ignore(x => x.DomainEvents);
            b.HasKey("Id");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex("Accepted", "State", "NextScanAt", "LeaseUntil", "Id");
        });
        m.Entity<TaskDispatch>(b =>
        {
            b.ToTable("dispatches", "tsk");
            b.HasKey("Id");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.WorkId).HasConversion(x => x.Value, x => new StrongId<TaskWork>(x));
            b.HasOne<TaskWork>().WithMany().HasForeignKey("WorkId").OnDelete(DeleteBehavior.Restrict);
            b.HasIndex("WorkId", "Sequence").IsUnique().HasDatabaseName("IX_task_dispatches_WorkId_Sequence");
        });
        m.Entity<TaskControlItem>(b =>
        {
            b.ToTable("control_items", "tsk");
            b.HasKey("WorkId", "TaskId");
            b.Property(x => x.WorkId).HasConversion(x => x.Value, x => new StrongId<TaskWork>(x));
            b.Property(x => x.WorkId).ValueGeneratedNever();
            b.Property(x => x.TaskId).HasConversion(x => x.Value, x => new StrongId<InstanceTask>(x));
            b.Property(x => x.TaskId).ValueGeneratedNever();
            b.HasOne<TaskWork>().WithMany().HasForeignKey("WorkId").OnDelete(DeleteBehavior.Restrict);
            b.HasOne<InstanceTask>().WithMany().HasForeignKey("TaskId").OnDelete(DeleteBehavior.Restrict);
        });
    }
}
