using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Svm.Services.CrossCutting.Registration;
using Svm.EventBus;
using Svm.Services.Contracts.Framework;
using Xunit;

namespace Svm.FrameworkTests;

[Trait("Category", "Business")]
public sealed class OutboxCompositionTests
{
    [Theory]
    [InlineData("remotePlaintext")]
    [InlineData("site")]
    [InlineData("batch")]
    [InlineData("host")]
    [InlineData("secret")]
    [InlineData("timeout")]
    public void InvalidOptionsAreRejectedBeforeAnyNetworkAccess(string fault)
    {
        var options = OutboxFixture.Options();
        options = fault switch { "remotePlaintext" => options with { Host = "broker.example.invalid" }, "site" => options with { SiteId = Guid.Empty },
            "batch" => options with { MessageDeliveryLimit = 1001 }, "host" => options with { Host = "amqp://user:secret@host" },
            "secret" => options with { Password = "" }, _ => options with { QueryTimeoutSeconds = 0 } };
        Assert.Equal(OutboxFailure.ConfigurationInvalid, Assert.Throws<OutboxException>(() => options.Validate()).Failure);
        Assert.DoesNotContain("fixture-secret", options.ToString());
    }
    [Fact]
    public async Task ConfigIsExplicitStrictAndRedacted()
    {
        Assert.Null(MessagingConfiguration.Load(null)); var options = OutboxFixture.Options();
        var path = await OutboxFixture.PrivateJsonAsync(options);
        try
        {
            Assert.Equal(options, MessagingConfiguration.Load(path));
            await File.WriteAllTextAsync(path, "{\"password\":\"private-diagnostic-secret\",\"unknown\":1}");
            var error = Assert.Throws<OutboxException>(() => MessagingConfiguration.Load(path));
            PersistenceDatabase.AssertRedacted(error.ToString(), "private-diagnostic-secret"); Assert.Null(error.InnerException);
            await File.WriteAllTextAsync(path, "{\"host\":\"127.0.0.1\",\"host\":\"other\"}"); Assert.Throws<OutboxException>(() => MessagingConfiguration.Load(path));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void DuplicateRegistrationAndHiddenHostedSendingAreRejected()
    {
        var services = new ServiceCollection(); services.AddSvmMessaging(OutboxFixture.Options(), false);
        Assert.Throws<OutboxException>(() => services.AddSvmMessaging(OutboxFixture.Options(), false));
        Assert.Single(services, d => d.ServiceType == typeof(IIntegrationEventOutbox) && d.Lifetime == ServiceLifetime.Scoped);
        Assert.DoesNotContain(services, d => d.ImplementationType?.Name.Contains("InboxCleanup", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(services, d => d.ImplementationType == typeof(MassTransitHostedService));
        Assert.DoesNotContain(services, d => d.ImplementationType?.Name.StartsWith("BusOutboxDelivery", StringComparison.Ordinal) == true);
    }
    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Transient)]
    public void FoundationRejectsAnOutboxOutsideTheOperationScope(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection(); services.AddSvmRequestPipeline([]).AddSvmMessaging(OutboxFixture.Options(), false);
        services.RemoveAll<IIntegrationEventOutbox>(); services.Add(new ServiceDescriptor(typeof(IIntegrationEventOutbox), typeof(InvalidOutbox), lifetime));
        Assert.Throws<InvalidOperationException>(() => services.ValidateSvmFoundation());
    }
    private sealed class InvalidOutbox : IIntegrationEventOutbox
    {
        public Task EnqueueAsync<T>(T message, CancellationToken cancellationToken) where T : class, IIntegrationEvent => throw new NotSupportedException();
    }
}
