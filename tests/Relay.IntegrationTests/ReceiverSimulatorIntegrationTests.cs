using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relay.Core;

namespace Relay.IntegrationTests;

public sealed class ReceiverSimulatorIntegrationTests
{
    private static readonly DateTimeOffset TestUtcNow =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReceiverValidatesSignaturesTimestampAndDuplicateBodyContract()
    {
        await using var factory = new ReceiverApplicationFactory(TestUtcNow);
        using var client = factory.CreateClient();
        using var createResponse = await client.PostAsJsonAsync(
            "/_control/receivers",
            new { behavior = "success" });
        var createJson = await createResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        using var createDocument = JsonDocument.Parse(createJson);
        var receiverId = createDocument.RootElement.GetProperty("id").GetGuid();
        var targetUrl = createDocument.RootElement.GetProperty("url").GetString();
        var signingSecret = createDocument.RootElement.GetProperty("signingSecret").GetString()!;
        Assert.Equal(
            $"http://receiver.test:8080/webhooks/{receiverId:D}",
            targetUrl);

        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");
        var timestamp = TestUtcNow.ToUnixTimeSeconds();
        var body = Encoding.UTF8.GetBytes(
            $"{{\"eventId\":\"{eventId:D}\",\"deliveryId\":\"{deliveryId:D}\",\"value\":1}}");
        var changedBody = Encoding.UTF8.GetBytes(
            $"{{\"eventId\":\"{eventId:D}\",\"deliveryId\":\"{deliveryId:D}\",\"value\":2}}");
        var invalidSigningSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var invalid = await SendSignedAsync(
            client,
            receiverId,
            eventId,
            deliveryId,
            correlationId,
            timestamp,
            invalidSigningSecret,
            body);
        var stale = await SendSignedAsync(
            client,
            receiverId,
            eventId,
            deliveryId,
            correlationId,
            timestamp - 301,
            signingSecret,
            body);
        var valid = await SendSignedAsync(
            client,
            receiverId,
            eventId,
            deliveryId,
            correlationId,
            timestamp,
            signingSecret,
            body);
        var duplicate = await SendSignedAsync(
            client,
            receiverId,
            eventId,
            deliveryId,
            correlationId,
            timestamp,
            signingSecret,
            body);
        var conflict = await SendSignedAsync(
            client,
            receiverId,
            eventId,
            deliveryId,
            correlationId,
            timestamp,
            signingSecret,
            changedBody);

        Assert.Equal(HttpStatusCode.Unauthorized, invalid);
        Assert.Equal(HttpStatusCode.Unauthorized, stale);
        Assert.Equal(HttpStatusCode.NoContent, valid);
        Assert.Equal(HttpStatusCode.NoContent, duplicate);
        Assert.Equal(HttpStatusCode.Conflict, conflict);

        using var receiptsResponse = await client.GetAsync(
            $"/_control/receivers/{receiverId:D}/receipts");
        var receiptsJson = await receiptsResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, receiptsResponse.StatusCode);
        Assert.False(receiptsJson.Contains(signingSecret, StringComparison.Ordinal));
        Assert.False(receiptsJson.Contains("signature", StringComparison.OrdinalIgnoreCase));
        Assert.False(receiptsJson.Contains("body", StringComparison.OrdinalIgnoreCase));

        using var receiptsDocument = JsonDocument.Parse(receiptsJson);
        var receipt = Assert.Single(receiptsDocument.RootElement.EnumerateArray());
        Assert.Equal(eventId, receipt.GetProperty("eventId").GetGuid());
        Assert.Equal(deliveryId, receipt.GetProperty("deliveryId").GetGuid());
        Assert.Equal(timestamp, receipt.GetProperty("timestamp").GetInt64());
        Assert.Equal(correlationId, receipt.GetProperty("correlationId").GetString());
        Assert.Equal(204, receipt.GetProperty("statusCode").GetInt32());
        Assert.Equal(2, receipt.GetProperty("receiveCount").GetInt32());
    }


    [Fact]
    public async Task RetryThenSucceedBehavior()
    {
        await using var factory = new ReceiverApplicationFactory(TestUtcNow);
        using var client = factory.CreateClient();
        using var createResponse = await client.PostAsJsonAsync(
            "/_control/receivers",
            new { behavior = "retryThenSucceed" });
        var createJson = await createResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        using var createDocument = JsonDocument.Parse(createJson);
        var receiverId = createDocument.RootElement.GetProperty("id").GetGuid();
        var signingSecret = createDocument.RootElement.GetProperty("signingSecret").GetString()!;
        var timestamp = TestUtcNow.ToUnixTimeSeconds();
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");
        var body = Encoding.UTF8.GetBytes(
            $"{{\"eventId\":\"{eventId:D}\",\"deliveryId\":\"{deliveryId:D}\",\"value\":1}}");

        var status1 = await SendSignedAsync(
            client, receiverId, eventId, deliveryId, correlationId,
            timestamp, signingSecret, body);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status1);

        var status2 = await SendSignedAsync(
            client, receiverId, eventId, deliveryId, correlationId,
            timestamp + 1, signingSecret, body);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status2);

        var status3 = await SendSignedAsync(
            client, receiverId, eventId, deliveryId, correlationId,
            timestamp + 2, signingSecret, body);
        Assert.Equal(HttpStatusCode.NoContent, status3);

        using var receiptsResponse = await client.GetAsync(
            $"/_control/receivers/{receiverId:D}/receipts");
        var receiptsJson = await receiptsResponse.Content.ReadAsStringAsync();
        using var receiptsDocument = JsonDocument.Parse(receiptsJson);
        var receipt = Assert.Single(receiptsDocument.RootElement.EnumerateArray());
        Assert.Equal(3, receipt.GetProperty("receiveCount").GetInt32());
        Assert.Equal(204, receipt.GetProperty("statusCode").GetInt32());
        Assert.False(receiptsJson.Contains(signingSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailUntilReplayBehavior()
    {
        await using var factory = new ReceiverApplicationFactory(TestUtcNow);
        using var client = factory.CreateClient();
        using var createResponse = await client.PostAsJsonAsync(
            "/_control/receivers",
            new { behavior = "failUntilReplay" });
        var createJson = await createResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        using var createDocument = JsonDocument.Parse(createJson);
        var receiverId = createDocument.RootElement.GetProperty("id").GetGuid();
        var signingSecret = createDocument.RootElement.GetProperty("signingSecret").GetString()!;
        var timestamp = TestUtcNow.ToUnixTimeSeconds();
        var eventId = Guid.NewGuid();
        var originalDeliveryId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");
        var body = Encoding.UTF8.GetBytes(
            $"{{\"eventId\":\"{eventId:D}\",\"deliveryId\":\"{originalDeliveryId:D}\",\"value\":1}}");

        for (var i = 0; i < 4; i++)
        {
            var status = await SendSignedAsync(
                client, receiverId, eventId, originalDeliveryId, correlationId,
                timestamp + i, signingSecret, body);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        }

        var replayDeliveryId = Guid.NewGuid();
        var replayBody = Encoding.UTF8.GetBytes(
            $"{{\"eventId\":\"{eventId:D}\",\"deliveryId\":\"{replayDeliveryId:D}\",\"value\":1}}");
        var replayStatus = await SendSignedAsync(
            client, receiverId, eventId, replayDeliveryId, correlationId,
            timestamp + 4, signingSecret, replayBody);
        Assert.Equal(HttpStatusCode.NoContent, replayStatus);

        using var receiptsResponse = await client.GetAsync(
            $"/_control/receivers/{receiverId:D}/receipts");
        var receiptsJson = await receiptsResponse.Content.ReadAsStringAsync();
        using var receiptsDocument = JsonDocument.Parse(receiptsJson);
        var receipts = receiptsDocument.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, receipts.Count);
        Assert.Equal(4, receipts[0].GetProperty("receiveCount").GetInt32());
        Assert.Equal(503, receipts[0].GetProperty("statusCode").GetInt32());
        Assert.Equal(1, receipts[1].GetProperty("receiveCount").GetInt32());
        Assert.Equal(204, receipts[1].GetProperty("statusCode").GetInt32());
        Assert.False(receiptsJson.Contains(signingSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AlwaysFailBehavior()
    {
        await using var factory = new ReceiverApplicationFactory(TestUtcNow);
        using var client = factory.CreateClient();
        using var createResponse = await client.PostAsJsonAsync(
            "/_control/receivers",
            new { behavior = "alwaysFail" });
        var createJson = await createResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        using var createDocument = JsonDocument.Parse(createJson);
        var receiverId = createDocument.RootElement.GetProperty("id").GetGuid();
        var signingSecret = createDocument.RootElement.GetProperty("signingSecret").GetString()!;
        var timestamp = TestUtcNow.ToUnixTimeSeconds();
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");
        var body = Encoding.UTF8.GetBytes(
            $"{{\"eventId\":\"{eventId:D}\",\"deliveryId\":\"{deliveryId:D}\",\"value\":1}}");

        for (var i = 0; i < 3; i++)
        {
            var status = await SendSignedAsync(
                client, receiverId, eventId, deliveryId, correlationId,
                timestamp + i, signingSecret, body);
            Assert.Equal(HttpStatusCode.InternalServerError, status);
        }

        using var receiptsResponse = await client.GetAsync(
            $"/_control/receivers/{receiverId:D}/receipts");
        var receiptsJson = await receiptsResponse.Content.ReadAsStringAsync();
        using var receiptsDocument = JsonDocument.Parse(receiptsJson);
        var receipt = Assert.Single(receiptsDocument.RootElement.EnumerateArray());
        Assert.Equal(3, receipt.GetProperty("receiveCount").GetInt32());
        Assert.Equal(500, receipt.GetProperty("statusCode").GetInt32());
        Assert.False(receiptsJson.Contains(signingSecret, StringComparison.Ordinal));
    }

    private static async Task<HttpStatusCode> SendSignedAsync(
        HttpClient client,
        Guid receiverId,
        Guid eventId,
        Guid deliveryId,
        string correlationId,
        long timestamp,
        string signingSecret,
        byte[] body)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/webhooks/{receiverId:D}");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        request.Headers.Add("X-Relay-Event-Id", eventId.ToString("D"));
        request.Headers.Add("X-Relay-Delivery-Id", deliveryId.ToString("D"));
        request.Headers.Add(
            "X-Relay-Timestamp",
            timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(
            "X-Relay-Signature",
            RelayRequestSigner.Sign(signingSecret, timestamp, deliveryId, body));
        request.Headers.Add("X-Correlation-Id", correlationId);

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }
}
