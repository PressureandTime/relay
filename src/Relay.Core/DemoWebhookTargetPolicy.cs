namespace Relay.Core;

public sealed class DemoWebhookTargetPolicy
{
    private const string WebhookPathPrefix = "/webhooks/";
    private readonly Uri _allowedOrigin;
    private readonly string _allowedAuthority;

    public DemoWebhookTargetPolicy(string receiverBaseUrl)
    {
        if (!Uri.TryCreate(receiverBaseUrl, UriKind.Absolute, out var origin)
            || !string.Equals(origin.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment)
            || origin.AbsolutePath is not "/")
        {
            throw new InvalidOperationException(
                "Relay:ReceiverBaseUrl must be an HTTP origin without a path, query, fragment, or user information.");
        }

        _allowedOrigin = origin;
        _allowedAuthority = origin.GetLeftPart(UriPartial.Authority);
    }

    public bool TryNormalize(
        string? candidate,
        out string normalizedUrl,
        out string validationError)
    {
        normalizedUrl = string.Empty;
        validationError = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            validationError = "A target URL is required.";
            return false;
        }

        var trimmedCandidate = candidate.Trim();
        if (trimmedCandidate.Length > RelayLimits.EndpointUrlLength
            || !Uri.TryCreate(trimmedCandidate, UriKind.Absolute, out var uri))
        {
            validationError = "The target URL is not a valid absolute URL.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || !string.Equals(uri.IdnHost, _allowedOrigin.IdnHost, StringComparison.OrdinalIgnoreCase)
            || uri.Port != _allowedOrigin.Port
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            validationError = "The target URL must use the configured Relay demo receiver origin.";
            return false;
        }

        var escapedPath = uri.AbsolutePath;
        if (!escapedPath.StartsWith(WebhookPathPrefix, StringComparison.Ordinal)
            || escapedPath.Contains('%', StringComparison.Ordinal))
        {
            validationError = "The target URL path must be /webhooks/{receiver-id}.";
            return false;
        }

        var receiverIdText = escapedPath[WebhookPathPrefix.Length..];
        if (receiverIdText.Contains('/', StringComparison.Ordinal)
            || !Guid.TryParseExact(receiverIdText, "D", out var receiverId))
        {
            validationError = "The target URL path must contain one canonical receiver identifier.";
            return false;
        }

        normalizedUrl = $"{_allowedAuthority}{WebhookPathPrefix}{receiverId:D}";
        if (!string.Equals(trimmedCandidate, normalizedUrl, StringComparison.OrdinalIgnoreCase))
        {
            normalizedUrl = string.Empty;
            validationError = "The target URL must use the canonical Relay demo receiver format.";
            return false;
        }

        return true;
    }
}
