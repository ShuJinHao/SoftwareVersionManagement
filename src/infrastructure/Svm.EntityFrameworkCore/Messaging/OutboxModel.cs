using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Svm.EntityFrameworkCore.Messaging;

internal static class OutboxModel
{
    internal static void Configure(ModelBuilder model)
    {
        model.AddInboxStateEntity(b => b.ToTable("InboxState", "framework"));
        model.AddOutboxStateEntity(b => b.ToTable("OutboxState", "framework"));
        model.AddOutboxMessageEntity(b => b.ToTable("OutboxMessage", "framework"));
    }
}
