namespace Relay.Core;

public enum DeliveryState
{
    Queued,
    Processing,
    RetryScheduled,
    Succeeded,
    Failed,
}
