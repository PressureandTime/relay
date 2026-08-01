namespace Relay.Core;

public static class RetryPolicy
{
    public static bool IsRetryable(string errorCode, int? httpStatusCode)
    {
        if (errorCode == "timeout" || errorCode == "transport_error" || errorCode == "claim_expired")
        {
            return true;
        }

        if (httpStatusCode.HasValue)
        {
            return httpStatusCode.Value == 408 || httpStatusCode.Value == 429 || (httpStatusCode.Value >= 500 && httpStatusCode.Value <= 599);
        }

        return false;
    }
}
