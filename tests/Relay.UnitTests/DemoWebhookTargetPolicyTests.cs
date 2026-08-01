using Relay.Core;

namespace Relay.UnitTests;

public sealed class DemoWebhookTargetPolicyTests
{
    private const string ReceiverOrigin = "http://receiver.test:8080";
    private const string ReceiverId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void TryNormalizeAcceptsCanonicalConfiguredReceiver()
    {
        var policy = new DemoWebhookTargetPolicy(ReceiverOrigin);
        var candidate = $"{ReceiverOrigin}/webhooks/{ReceiverId}";

        var accepted = policy.TryNormalize(candidate, out var normalizedUrl, out var error);

        Assert.True(accepted);
        Assert.Equal(candidate, normalizedUrl);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("https://receiver.test:8080/webhooks/11111111-2222-3333-4444-555555555555")]
    [InlineData("http://alternate.test:8080/webhooks/11111111-2222-3333-4444-555555555555")]
    [InlineData("http://receiver.test:8081/webhooks/11111111-2222-3333-4444-555555555555")]
    [InlineData("http://user:password@receiver.test:8080/webhooks/11111111-2222-3333-4444-555555555555")]
    [InlineData("http://receiver.test:8080/webhooks/11111111-2222-3333-4444-555555555555?mode=test")]
    [InlineData("http://receiver.test:8080/webhooks/11111111-2222-3333-4444-555555555555#fragment")]
    [InlineData("http://receiver.test:8080/webhooks/%311111111-2222-3333-4444-555555555555")]
    [InlineData("http://receiver.test:8080/webhooks/./11111111-2222-3333-4444-555555555555")]
    [InlineData("http://receiver.test:8080/webhooks/11111111-2222-3333-4444-555555555555/")]
    [InlineData("http://receiver.test:8080/webhooks/11111111-2222-3333-4444-555555555555/extra")]
    public void TryNormalizeRejectsNonCanonicalOrNonAllowlistedTarget(string candidate)
    {
        var policy = new DemoWebhookTargetPolicy(ReceiverOrigin);

        var accepted = policy.TryNormalize(candidate, out var normalizedUrl, out var error);

        Assert.False(accepted);
        Assert.Empty(normalizedUrl);
        Assert.NotEmpty(error);
    }
}
