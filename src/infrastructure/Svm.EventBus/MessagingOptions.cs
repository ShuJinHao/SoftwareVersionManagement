using Svm.Services.Contracts.Framework;

namespace Svm.EventBus;

public sealed record MessagingOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 5671;
    public required string VirtualHost { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required Guid SiteId { get; init; }
    public bool UseTls { get; init; } = true;
    public string? TlsServerName { get; init; }
    public int QueryDelaySeconds { get; init; } = 10;
    public int QueryMessageLimit { get; init; } = 100;
    public int QueryTimeoutSeconds { get; init; } = 30;
    public int MessageDeliveryLimit { get; init; } = 100;
    public int MessageDeliveryTimeoutSeconds { get; init; } = 10;
    public int ConsumerConcurrency { get; init; } = 4;
    public int ConsumerPrefetch { get; init; } = 16;
    public int InboxWindowMinutes { get; init; } = 30;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Uri.CheckHostName(Host) == UriHostNameType.Unknown ||
            Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(VirtualHost) || VirtualHost.Length > 128 ||
            string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password) || SiteId == Guid.Empty ||
            !UseTls && Host is not ("127.0.0.1" or "::1" or "localhost") ||
            UseTls && string.IsNullOrWhiteSpace(TlsServerName) ||
            QueryDelaySeconds is < 1 or > 60 || QueryMessageLimit is < 1 or > 1000 ||
            QueryTimeoutSeconds is < 1 or > 120 || MessageDeliveryLimit is < 1 or > 1000 ||
            MessageDeliveryTimeoutSeconds is < 1 or > 120 || ConsumerConcurrency is < 1 or > 64 ||
            ConsumerPrefetch < ConsumerConcurrency || ConsumerPrefetch > 256 || InboxWindowMinutes is < 1 or > 1440)
            throw new OutboxException(OutboxFailure.ConfigurationInvalid);
    }
    public override string ToString() => "SVM messaging configuration (redacted)";
}
