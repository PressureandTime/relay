namespace Relay.Core;

public sealed class DeliveryAttempt
{
    private DeliveryAttempt()
    {
    }

    public DeliveryAttempt(
        Guid id,
        Guid deliveryId,
        int attemptNumber,
        DateTimeOffset startedAtUtc)
    {
        Id = id;
        DeliveryId = deliveryId;
        AttemptNumber = attemptNumber;
        State = AttemptState.Processing;
        StartedAtUtc = startedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid DeliveryId { get; private set; }

    public Delivery Delivery { get; private set; } = null!;

    public int AttemptNumber { get; private set; }

    public AttemptState State { get; private set; }

    public int? HttpStatusCode { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public long? DurationMilliseconds { get; private set; }

    public void MarkSucceeded(
        int httpStatusCode,
        DateTimeOffset completedAtUtc,
        long durationMilliseconds)
    {
        EnsureProcessing();
        State = AttemptState.Succeeded;
        HttpStatusCode = httpStatusCode;
        CompletedAtUtc = completedAtUtc;
        DurationMilliseconds = durationMilliseconds;
    }

    public void MarkFailed(
        int? httpStatusCode,
        string errorCode,
        string errorMessage,
        DateTimeOffset completedAtUtc,
        long durationMilliseconds)
    {
        EnsureProcessing();
        State = AttemptState.Failed;
        HttpStatusCode = httpStatusCode;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        CompletedAtUtc = completedAtUtc;
        DurationMilliseconds = durationMilliseconds;
    }

    private void EnsureProcessing()
    {
        if (State is not AttemptState.Processing)
        {
            throw new InvalidOperationException("Only processing attempts can be completed.");
        }
    }
}
