namespace Relay.Core;

public sealed class WebhookEvent
{
    private WebhookEvent()
    {
    }

    public WebhookEvent(
        Guid id,
        Guid endpointId,
        string eventType,
        string payloadJson,
        string idempotencyKey,
        string requestFingerprint,
        string correlationId,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        EndpointId = endpointId;
        EventType = eventType;
        PayloadJson = payloadJson;
        IdempotencyKey = idempotencyKey;
        RequestFingerprint = requestFingerprint;
        CorrelationId = correlationId;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid EndpointId { get; private set; }

    public WebhookEndpoint Endpoint { get; private set; } = null!;

    public string EventType { get; private set; } = string.Empty;

    public string PayloadJson { get; private set; } = "{}";

    public string IdempotencyKey { get; private set; } = string.Empty;

    public string RequestFingerprint { get; private set; } = string.Empty;

    public string CorrelationId { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }
}
