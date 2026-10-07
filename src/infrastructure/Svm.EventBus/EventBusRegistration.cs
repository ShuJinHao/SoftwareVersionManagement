using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Svm.EntityFrameworkCore.Messaging;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Messaging.V1;

namespace Svm.EventBus;

public static class EventBusRegistration
{
    public static IServiceCollection AddSvmMessaging(this IServiceCollection services, MessagingOptions options, bool delivery,
        IReadOnlyList<IntegrationConsumerBinding>? consumers = null)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        if (services.Any(s => s.ServiceType == typeof(IIntegrationEventOutbox) || s.ServiceType == typeof(MessagingOptions)))
            throw new OutboxException(OutboxFailure.ConfigurationInvalid);
        services.AddSingleton(options);
        services.AddLogging();
        services.AddScoped<IIntegrationEventOutbox, IntegrationEventOutbox>();
        var bindings = (consumers ?? []).ToArray();
        if (bindings.Any(b => b is null) || bindings.Select(b => b.EventType).Distinct().Count() != bindings.Length ||
            bindings.Length > 0 && (!delivery || !services.Any(s => s.ServiceType == typeof(IIntegrationEventPreflight)) ||
                !services.Any(s => s.ServiceType == typeof(IIntegrationConsumptionRecovery))))
            throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
        var dispatchers = services.Where(s => s.ServiceType.IsGenericType && s.ServiceType.GetGenericTypeDefinition() == typeof(IIntegrationEventDispatcher<>)).ToArray();
        if (dispatchers.Length != bindings.Length || !dispatchers.Select(s => s.ServiceType.GenericTypeArguments[0]).ToHashSet().SetEquals(bindings.Select(b => b.EventType)))
            throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
        foreach (var binding in bindings)
        {
            _ = IntegrationEventDescriptor.For(binding.EventType);
            var handlers = services.Where(s => s.ServiceType == binding.HandlerType).ToArray();
            if (handlers.Length != 1 || handlers[0].IsKeyedService || handlers[0].Lifetime != ServiceLifetime.Scoped ||
                handlers[0].ImplementationType != binding.HandlerType ||
                !binding.HandlerType.GetInterfaces().Contains(typeof(IIntegrationEventHandler<>).MakeGenericType(binding.EventType)))
                throw new IntegrationConsumptionException(ConsumptionFailure.ConfigurationInvalid);
        }
        services.AddMassTransit(registration =>
        {
            registration.AddSvmTransactionalOutbox(delivery, TimeSpan.FromSeconds(options.QueryDelaySeconds), options.QueryMessageLimit,
                TimeSpan.FromSeconds(options.QueryTimeoutSeconds), options.MessageDeliveryLimit, TimeSpan.FromSeconds(options.MessageDeliveryTimeoutSeconds),
                bindings.Length > 0, TimeSpan.FromMinutes(options.InboxWindowMinutes));
            foreach (var binding in bindings)
            {
                if (binding.EventType == typeof(PackageWorkAvailableV1)) registration.AddConsumer<IntegrationConsumer<PackageWorkAvailableV1>>();
                else if (binding.EventType == typeof(TaskPreparationAvailableV1)) registration.AddConsumer<IntegrationConsumer<TaskPreparationAvailableV1>>();
                else registration.AddConsumer<IntegrationConsumer<TaskControlAvailableV1>>();
            }
            registration.UsingRabbitMq((context, bus) =>
            {
                bus.SetQueueArgument("x-queue-type", "classic");
                bus.Host(options.Host, (ushort)options.Port, options.VirtualHost, host =>
                {
                    host.Username(options.Username); host.Password(options.Password);
                    if (options.UseTls) host.UseSsl(ssl => ssl.ServerName = options.TlsServerName!);
                });
                bus.Message<PackageWorkAvailableV1>(m => m.SetEntityName("svm.package-work.available.v1"));
                bus.Message<TaskPreparationAvailableV1>(m => m.SetEntityName("svm.task-preparation.available.v1"));
                bus.Message<TaskControlAvailableV1>(m => m.SetEntityName("svm.task-control.available.v1"));
                foreach (var binding in bindings)
                    bus.ReceiveEndpoint(IntegrationEventDescriptor.For(binding.EventType).Queue, endpoint =>
                    {
                        endpoint.ConcurrentMessageLimit = options.ConsumerConcurrency;
                        endpoint.PrefetchCount = (ushort)options.ConsumerPrefetch;
                        endpoint.SetQuorumQueue();
                        endpoint.ConfigureDeadLetter(pipe => pipe.UseFilter(new UnknownIntegrationContractFilter()));
                        endpoint.ConfigureConsumeTopology = false;
                        endpoint.UseMessageRetry(retry =>
                        {
                            retry.Handle<Exception>(ConsumptionRetry.CanRetry);
                            retry.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
                        });
                        endpoint.UseConsumeFilter(typeof(IntegrationPreflightFilter<>), context);
                        endpoint.UseSvmConsumerOutbox(context, options.MessageDeliveryLimit, TimeSpan.FromSeconds(options.MessageDeliveryTimeoutSeconds));
                        if (binding.EventType == typeof(PackageWorkAvailableV1)) endpoint.ConfigureConsumer<IntegrationConsumer<PackageWorkAvailableV1>>(context);
                        else if (binding.EventType == typeof(TaskPreparationAvailableV1)) endpoint.ConfigureConsumer<IntegrationConsumer<TaskPreparationAvailableV1>>(context);
                        else endpoint.ConfigureConsumer<IntegrationConsumer<TaskControlAvailableV1>>(context);
                    });
            });
        });
        services.Configure<MassTransitHostOptions>(host => { host.WaitUntilStarted = false; host.StopTimeout = TimeSpan.FromSeconds(10); });
        if (!delivery) services.RemoveHostedService<MassTransitHostedService>(); // API stages without opening a broker connection.
        return services;
    }
}
