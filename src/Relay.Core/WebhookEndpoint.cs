namespace Relay.Core;

public sealed class WebhookEndpoint
{
    private WebhookEndpoint()
    {
    }

    public WebhookEndpoint(
        Guid id,
        string name,
        string targetUrl,
        string protectedSigningSecret,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Name = name;
        TargetUrl = targetUrl;
        ProtectedSigningSecret = protectedSigningSecret;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string TargetUrl { get; private set; } = string.Empty;

    public string ProtectedSigningSecret { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }
}
