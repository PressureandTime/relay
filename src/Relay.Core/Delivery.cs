namespace Relay.Core;

public sealed class Delivery
{
    private Delivery()
    {
    }

    public Delivery(
        Guid id,
        Guid eventId,
        Guid endpointId,
        string envelopeJson,
        string envelopeHash,
        string correlationId,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        EventId = eventId;
        EndpointId = endpointId;
        EnvelopeJson = envelopeJson;
        EnvelopeHash = envelopeHash;
        CorrelationId = correlationId;
        State = DeliveryState.Queued;
        CreatedAtUtc = createdAtUtc;
    }

    public Delivery(
        Guid id,
        Guid eventId,
        Guid endpointId,
        string envelopeJson,
        string envelopeHash,
        string correlationId,
        Guid replayOfDeliveryId,
        string replayIdempotencyKey,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        EventId = eventId;
        EndpointId = endpointId;
        EnvelopeJson = envelopeJson;
        EnvelopeHash = envelopeHash;
        CorrelationId = correlationId;
        ReplayOfDeliveryId = replayOfDeliveryId;
        ReplayIdempotencyKey = replayIdempotencyKey;
        State = DeliveryState.Queued;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    public DateTimeOffset? ClaimedAtUtc { get; private set; }

    public DateTimeOffset? ClaimExpiresAtUtc { get; private set; }

    public Guid? ReplayOfDeliveryId { get; private set; }

    public string? ReplayIdempotencyKey { get; private set; }

    public Guid EventId { get; private set; }

    public WebhookEvent Event { get; private set; } = null!;

    public Guid EndpointId { get; private set; }

    public WebhookEndpoint Endpoint { get; private set; } = null!;

    public DeliveryState State { get; private set; }

    public string EnvelopeJson { get; private set; } = "{}";

    public string EnvelopeHash { get; private set; } = string.Empty;

    public Guid? ClaimToken { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public void Claim(Guid claimToken, DateTimeOffset startedAtUtc)
    {
        if (State is not DeliveryState.Queued && State is not DeliveryState.RetryScheduled)
        {
            throw new InvalidOperationException("Only queued or retry-scheduled deliveries can be claimed.");
        }

        ClaimToken = claimToken;
        StartedAtUtc = startedAtUtc;
        ClaimedAtUtc = startedAtUtc;
        ClaimExpiresAtUtc = startedAtUtc.Add(RelayLimits.ClaimLeaseDuration);
        AttemptCount++;
        NextAttemptAtUtc = null;
        State = DeliveryState.Processing;
    }

    public void MarkSucceeded(Guid claimToken, DateTimeOffset completedAtUtc)
    {
        EnsureClaim(claimToken);
        State = DeliveryState.Succeeded;
        CompletedAtUtc = completedAtUtc;
        ClaimToken = null;
        ClaimedAtUtc = null;
        ClaimExpiresAtUtc = null;
        ErrorCode = null;
        ErrorMessage = null;
    }

    public void MarkFailed(
        Guid claimToken,
        string errorCode,
        string errorMessage,
        DateTimeOffset completedAtUtc)
    {
        EnsureClaim(claimToken);
        State = DeliveryState.Failed;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        CompletedAtUtc = completedAtUtc;
        ClaimToken = null;
        ClaimedAtUtc = null;
        ClaimExpiresAtUtc = null;
    }

    public void ScheduleRetry(DateTimeOffset nextAttemptAtUtc)
    {
        if (State is not DeliveryState.Processing)
        {
            throw new InvalidOperationException("Only processing deliveries can be scheduled for retry.");
        }

        State = DeliveryState.RetryScheduled;
        NextAttemptAtUtc = nextAttemptAtUtc;
        ClaimToken = null;
        ClaimedAtUtc = null;
        ClaimExpiresAtUtc = null;
    }

    public void RecoverStaleClaim(DateTimeOffset now)
    {
        if (State is not DeliveryState.Processing || ClaimExpiresAtUtc >= now)
        {
            throw new InvalidOperationException("Only processing deliveries with expired claims can be recovered.");
        }

        ScheduleRetry(now);
    }

    private void EnsureClaim(Guid claimToken)
    {
        if (State is not DeliveryState.Processing || ClaimToken != claimToken)
        {
            throw new InvalidOperationException("The delivery claim does not match.");
        }
    }
}
