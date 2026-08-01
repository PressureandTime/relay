using System.Text;
using Relay.Core;

namespace Relay.UnitTests;

public sealed class RelayRequestSignerTests
{
    [Fact]
    public void SignMatchesGoldenExactByteVector()
    {
        const string secret = "relay-test-secret-1234567890";
        const long timestamp = 1_767_225_600;
        var deliveryId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var body = Encoding.UTF8.GetBytes(
            "{\"type\":\"demo.created\",\"payload\":{\"value\":42}}");

        var signature = RelayRequestSigner.Sign(secret, timestamp, deliveryId, body);

        Assert.Equal(
            "v1=dh4kpcv50+7htYgNDHkygy4HCW1+437h4aN5wGdTS8E=",
            signature);
    }
}
