namespace Relay.Core;

public static class RelayLimits
{
    public const int EndpointNameLength = 100;
    public const int EndpointUrlLength = 2048;
    public const int ProtectedSecretLength = 4096;
    public const int SigningSecretMinimumLength = 16;
    public const int SigningSecretLength = 256;
    public const int EventTypeLength = 100;
    public const int IdempotencyKeyLength = 128;
    public const int FingerprintLength = 64;
    public const int CorrelationIdLength = 64;
    public const int EnvelopeHashLength = 64;
    public const int ErrorCodeLength = 64;
    public const int ErrorMessageLength = 512;
    public const int MaximumPayloadBytes = 64 * 1024;
    public const int MaxDeliveryAttempts = 4;
    public static readonly TimeSpan ClaimLeaseDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan[] RetryDelays = [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];
}
