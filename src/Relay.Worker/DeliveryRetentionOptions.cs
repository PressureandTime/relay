namespace Relay.Worker;

public sealed class DeliveryRetentionOptions
{
    public const string SectionName = "Relay:DeliveryRetention";

    public bool Enabled { get; init; } = true;

    public TimeSpan RetainFor { get; init; } = TimeSpan.FromDays(30);

    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromHours(1);
}
