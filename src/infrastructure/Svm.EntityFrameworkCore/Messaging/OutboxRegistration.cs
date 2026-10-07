using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MassTransit.Middleware;
using Svm.EntityFrameworkCore.Framework;
using Svm.Services.Contracts.Framework;

namespace Svm.EntityFrameworkCore.Messaging;

/// <summary>Infrastructure-only capability; no context, connection or save method crosses this bridge.</summary>
public interface IOutboxTransactionGuard { void EnsureCanEnqueue(); }

internal sealed class OutboxTransactionGuard(SvmDbContext context, IUnitOfWork unit) : IOutboxTransactionGuard
{
    public void EnsureCanEnqueue()
    {
        if (unit.CurrentOperationId is null || context.Database.CurrentTransaction is null || context.OutboxSealed)
            throw new OutboxException(OutboxFailure.TransactionRequired);
    }
}

public static class OutboxRegistration
{
    public static void AddSvmTransactionalOutbox(this IBusRegistrationConfigurator registration, bool delivery,
        TimeSpan queryDelay, int queryLimit, TimeSpan queryTimeout, int deliveryLimit, TimeSpan deliveryTimeout,
        bool consumption = false, TimeSpan? inboxWindow = null)
    {
        registration.AddScoped<IOutboxTransactionGuard, OutboxTransactionGuard>();
        registration.AddEntityFrameworkOutbox<SvmDbContext>(outbox =>
        {
            outbox.UsePostgres(enableSchemaCaching: false);
            outbox.QueryDelay = queryDelay; outbox.QueryMessageLimit = queryLimit; outbox.QueryTimeout = queryTimeout;
            outbox.DuplicateDetectionWindow = inboxWindow ?? TimeSpan.FromMinutes(30);
            outbox.DisableInboxCleanupService();
            outbox.UseBusOutbox(bus =>
            {
                bus.MessageDeliveryLimit = deliveryLimit; bus.MessageDeliveryTimeout = deliveryTimeout;
                bus.DisableDeliveryService();
            });
        });
        if (consumption)
        {
            registration.AddScoped<IIntegrationConsumptionTransaction, ConsumerTransactionGuard>();
            registration.AddScoped<EntityFrameworkOutboxContextFactory<SvmDbContext>>();
            registration.Replace(ServiceDescriptor.Scoped<IOutboxContextFactory<SvmDbContext>, ConsumerOutboxContextFactory>());
            registration.AddScoped<OutboxDeliveryContext>();
            registration.AddHostedService<InboxCleanupService<OutboxDeliveryContext>>();
        }
        if (delivery)
        {
            registration.TryAddScoped<OutboxDeliveryContext>();
            registration.AddHostedService<BusOutboxDeliveryService<OutboxDeliveryContext>>();
        }
    }

    public static void UseSvmConsumerOutbox(this IReceiveEndpointConfigurator endpoint, IRegistrationContext context,
        int deliveryLimit, TimeSpan deliveryTimeout) => endpoint.UseEntityFrameworkOutbox<SvmDbContext>(context, options =>
        {
            options.MessageDeliveryLimit = deliveryLimit; options.MessageDeliveryTimeout = deliveryTimeout;
        });
}
