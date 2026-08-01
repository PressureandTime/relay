namespace Relay.ReceiverSimulator;

public sealed record CreateReceiverRequest(string? Behavior);

public sealed record CreateReceiverResponse(
    Guid Id,
    string Url,
    string SigningSecret);

public sealed record ReceiverReceiptResponse(
    Guid EventId,
    Guid DeliveryId,
    long Timestamp,
    string CorrelationId,
    int StatusCode,
    int ReceiveCount,
    DateTimeOffset ReceivedAtUtc);

public sealed class ReceiverSimulatorOptions
{
    public const string SectionName = "Relay";
    public const string DefaultPublicBaseUrl = "http://receiver:8080";

    public string PublicBaseUrl { get; init; } = DefaultPublicBaseUrl;

    public static bool HasValidPublicBaseUrl(ReceiverSimulatorOptions options) =>
        TryNormalizePublicBaseUrl(options.PublicBaseUrl, out _);

    internal static bool TryNormalizePublicBaseUrl(string? candidate, out string normalizedBaseUrl)
    {
        normalizedBaseUrl = string.Empty;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var publicBaseUri)
            || !string.Equals(publicBaseUri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || string.IsNullOrEmpty(publicBaseUri.Host)
            || !string.IsNullOrEmpty(publicBaseUri.UserInfo)
            || !string.IsNullOrEmpty(publicBaseUri.Query)
            || !string.IsNullOrEmpty(publicBaseUri.Fragment)
            || publicBaseUri.AbsolutePath is not "/")
        {
            return false;
        }

        normalizedBaseUrl = publicBaseUri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
