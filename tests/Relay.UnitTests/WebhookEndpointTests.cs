using Relay.Core;

namespace Relay.UnitTests;

public sealed class WebhookEndpointTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewEndpointIsActive()
    {
        var endpoint = CreateEndpoint();

        Assert.Equal(EndpointState.Active, endpoint.State);
    }

    [Fact]
    public void DisableAndReactivateAreIdempotent()
    {
        var endpoint = CreateEndpoint();
        endpoint.Disable();
        endpoint.Disable();

        Assert.Equal(EndpointState.Disabled, endpoint.State);

        endpoint.Reactivate();
        endpoint.Reactivate();

        Assert.Equal(EndpointState.Active, endpoint.State);
    }

    private static WebhookEndpoint CreateEndpoint() =>
        new(
            Guid.NewGuid(),
            "Synthetic receiver",
            "http://receiver.test:8080/webhooks/11111111-1111-1111-1111-111111111111",
            "protected-signing-secret",
            CreatedAtUtc);
}
