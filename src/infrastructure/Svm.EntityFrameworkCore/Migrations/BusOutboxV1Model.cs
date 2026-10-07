using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;

namespace Svm.EntityFrameworkCore.Migrations;

// Frozen 8.3.6 shape. Do not delegate historical migration targets to the live OutboxModel.
internal static class BusOutboxV1Model
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<InboxState>(b =>
        {
            b.Property(x => x.Id).ValueGeneratedOnAdd().UseIdentityByDefaultColumn(); b.HasKey(x => x.Id);
            b.Property(x => x.MessageId); b.Property(x => x.ConsumerId); b.HasAlternateKey(x => new { x.MessageId, x.ConsumerId });
            b.Property(x => x.LockId); b.Property(x => x.RowVersion).IsRowVersion();
            b.Property(x => x.Received); b.Property(x => x.ReceiveCount); b.Property(x => x.ExpirationTime); b.Property(x => x.Consumed);
            b.Property(x => x.Delivered); b.HasIndex(x => x.Delivered); b.Property(x => x.LastSequenceNumber);
            b.ToTable("InboxState", "framework");
        });
        model.Entity<OutboxState>(b =>
        {
            b.Property(x => x.OutboxId); b.HasKey(x => x.OutboxId); b.Property(x => x.LockId); b.Property(x => x.RowVersion).IsRowVersion();
            b.Property(x => x.Created); b.HasIndex(x => x.Created); b.Property(x => x.Delivered); b.Property(x => x.LastSequenceNumber);
            b.ToTable("OutboxState", "framework");
        });
        model.Entity<OutboxMessage>(b =>
        {
            b.Property(x => x.SequenceNumber).ValueGeneratedOnAdd().UseIdentityByDefaultColumn(); b.HasKey(x => x.SequenceNumber); b.Property(x => x.MessageId);
            b.Property(x => x.ConversationId); b.Property(x => x.CorrelationId); b.Property(x => x.InitiatorId); b.Property(x => x.RequestId);
            b.Property(x => x.SourceAddress).HasMaxLength(256); b.Property(x => x.DestinationAddress).HasMaxLength(256);
            b.Property(x => x.ResponseAddress).HasMaxLength(256); b.Property(x => x.FaultAddress).HasMaxLength(256);
            b.Property(x => x.ExpirationTime); b.HasIndex(x => x.ExpirationTime); b.Property(x => x.EnqueueTime); b.HasIndex(x => x.EnqueueTime);
            b.Property(x => x.SentTime); b.Property(x => x.InboxMessageId); b.Property(x => x.InboxConsumerId);
            b.HasIndex(x => new { x.InboxMessageId, x.InboxConsumerId, x.SequenceNumber }).IsUnique();
            b.HasOne<InboxState>().WithMany().IsRequired(false).HasForeignKey(x => new { x.InboxMessageId, x.InboxConsumerId })
                .HasPrincipalKey(x => new { x.MessageId, x.ConsumerId });
            b.Property(x => x.OutboxId).IsRequired(false); b.HasIndex(x => new { x.OutboxId, x.SequenceNumber }).IsUnique();
            b.HasOne<OutboxState>().WithMany().IsRequired(false).HasForeignKey(x => x.OutboxId);
            b.Property(x => x.Headers); b.Property(x => x.Properties); b.Property(x => x.ContentType).IsRequired().HasMaxLength(256);
            b.Property(x => x.MessageType).IsRequired(); b.Property(x => x.Body).IsRequired(); b.ToTable("OutboxMessage", "framework");
        });
    }
}
