using System.Text.Json;
using MassTransit;
using MassTransit.Middleware;
using MassTransit.Middleware.Rescue;
using Svm.Services.Contracts.Framework;

namespace Svm.EventBus;

internal sealed class IntegrationConsumer<TEvent>(IIntegrationEventDispatcher<TEvent> dispatcher) : IConsumer<TEvent>
    where TEvent : class, IIntegrationEvent
{
    public async Task Consume(ConsumeContext<TEvent> context) => await dispatcher.DispatchAsync(context.Message, context.CancellationToken);
}

// Resolves only the preflight port; the handler/outgoing send provider is resolved inside the native outbox scope.
internal sealed class IntegrationPreflightFilter<TEvent>(IIntegrationEventPreflight verification, MessagingOptions options)
    : IFilter<ConsumeContext<TEvent>> where TEvent : class
{
    public async Task Send(ConsumeContext<TEvent> context, IPipe<ConsumeContext<TEvent>> next)
    {
        if (context.Message is not IIntegrationEvent message || message.SiteId != options.SiteId ||
            context.MessageId != message.EventId || context.GetOriginalMessageId() != message.EventId)
            throw new IntegrationConsumptionException(ConsumptionFailure.InvalidMessage);
        try
        {
            using var json = JsonDocument.Parse(context.ReceiveContext.GetBody());
            var root = json.RootElement;
            var body = root.GetProperty("message");
            if (root.ValueKind != JsonValueKind.Object || body.ValueKind != JsonValueKind.Object ||
                DuplicateProperties(root) || DuplicateProperties(body) || root.GetProperty("messageId").GetGuid() != message.EventId ||
                body.GetProperty("eventId").GetGuid() != message.EventId || body.GetProperty("schemaVersion").GetInt32() != 1 ||
                body.GetProperty("messageType").GetString() != IntegrationEventDescriptor.For(message.GetType()).Queue ||
                !root.GetProperty("messageType").EnumerateArray().Any(t => t.GetString() == MessageUrn.ForType<TEvent>().ToString()))
                throw new IntegrationConsumptionException(ConsumptionFailure.InvalidMessage);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new IntegrationConsumptionException(ConsumptionFailure.InvalidMessage); }
        await verification.VerifyAsync(message, context.CancellationToken);
        await next.Send(context);
    }
    private static bool DuplicateProperties(JsonElement obj) => obj.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != obj.EnumerateObject().Count();
    public void Probe(ProbeContext context) => context.CreateFilterScope("svmCurrentWorkPreflight");
}

internal static class ConsumptionRetry
{
    // SQLSTATE proves a transaction abort. Generic network errors cannot prove non-commit.
    internal static bool CanRetry(Exception exception) => exception is PersistenceException
        { Failure: PersistenceFailure.DependencyUnavailable, SqlState: "40001" or "40P01" } or
        IntegrationConsumptionException { Failure: ConsumptionFailure.TransientClaimConflict };
}

// In the pinned version dead-letter filters surround the default error rescue. Throwing here would requeue forever.
internal sealed class UnknownIntegrationContractFilter : IFilter<ReceiveContext>
{
    private readonly IPipe<ExceptionReceiveContext> _error = Pipe.New<ExceptionReceiveContext>(pipe =>
    {
        pipe.UseFilter(new GenerateFaultFilter()); pipe.UseFilter(new ErrorTransportFilter());
    });
    public async Task Send(ReceiveContext context, IPipe<ReceiveContext> next)
    {
        await _error.Send(new RescueExceptionReceiveContext(context, new IntegrationConsumptionException(ConsumptionFailure.InvalidMessage)));
        // Error transport completed the quarantine; continuing would also copy to the skipped queue.
    }
    public void Probe(ProbeContext context) => context.CreateFilterScope("svmUnknownContractErrorTransport");
}
