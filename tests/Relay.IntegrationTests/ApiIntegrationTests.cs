using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Relay.Core;
using Relay.Infrastructure;

namespace Relay.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class ApiIntegrationTests : IAsyncLifetime, IDisposable
{
    private static readonly DateTimeOffset TestUtcNow =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlFixture _database;
    private ApiApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private bool _disposed;

    public ApiIntegrationTests(PostgreSqlFixture database)
    {
        _database = database;
    }

    public async Task InitializeAsync()
    {
        await _database.ResetAsync();
        _factory = new ApiApplicationFactory(_database.ConnectionString, TestUtcNow);
        _client = _factory.CreateClient();
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _client?.Dispose();
        _factory?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task MigrationsApplyToAnEmptyPostgreSqlDatabase()
    {
        await using var database = _database.CreateDbContext();
        var appliedMigrations = (await database.Database.GetAppliedMigrationsAsync()).ToArray();
        var pendingMigrations = (await database.Database.GetPendingMigrationsAsync()).ToArray();

        Assert.Equal(0, _database.AppliedMigrationCountBeforeMigration);
        Assert.Equal(3, _database.AvailableMigrationCount);
        Assert.Equal(
            _database.AvailableMigrationCount,
            _database.AppliedMigrationCountAfterMigration);
        Assert.Equal(_database.AvailableMigrationCount, appliedMigrations.Length);
        Assert.Empty(pendingMigrations);
        Assert.Equal(0, await database.WebhookEndpoints.CountAsync());
        Assert.Equal(0, await database.WebhookEvents.CountAsync());
        Assert.Equal(0, await database.Deliveries.CountAsync());
        Assert.Equal(0, await database.DeliveryAttempts.CountAsync());
    }

    [Fact]
    public async Task CreatingAndListingEndpointsProtectsAndNeverReturnsTheSecret()
    {
        var endpoint = await CreateEndpointAsync();

        Assert.False(endpoint.RawResponse.Contains(endpoint.SigningSecret, StringComparison.Ordinal));
        Assert.False(endpoint.RawResponse.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            ["createdAtUtc", "id", "name", "url"],
            GetPropertyNames(endpoint.RawResponse));

        await using (var database = _database.CreateDbContext())
        {
            var storedEndpoint = await database.WebhookEndpoints.AsNoTracking().SingleAsync();
            Assert.NotEqual(endpoint.SigningSecret, storedEndpoint.ProtectedSigningSecret);
            Assert.False(
                storedEndpoint.ProtectedSigningSecret.Contains(
                    endpoint.SigningSecret,
                    StringComparison.Ordinal));

            using var scope = _factory.Services.CreateScope();
            var protector = scope.ServiceProvider.GetRequiredService<IEndpointSecretProtector>();
            Assert.Equal(
                endpoint.SigningSecret,
                protector.Unprotect(storedEndpoint.ProtectedSigningSecret));
        }

        using var listResponse = await _client.GetAsync("/api/endpoints");
        var listJson = await listResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.False(listJson.Contains(endpoint.SigningSecret, StringComparison.Ordinal));
        Assert.False(listJson.Contains("secret", StringComparison.OrdinalIgnoreCase));
        using var listDocument = JsonDocument.Parse(listJson);
        var listedEndpoint = Assert.Single(listDocument.RootElement.EnumerateArray());
        Assert.Equal(endpoint.Id, listedEndpoint.GetProperty("id").GetGuid());
        Assert.Equal(endpoint.Url, listedEndpoint.GetProperty("url").GetString());
        Assert.Equal(
            ["createdAtUtc", "id", "name", "url"],
            listedEndpoint.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task CreatingAnEventPersistsExactlyOneEventAndDelivery()
    {
        var endpoint = await CreateEndpointAsync();
        var idempotencyKey = $"event:{Guid.NewGuid():N}";

        var submission = await SubmitEventAsync(
            endpoint.Id,
            idempotencyKey,
            $"payload-{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.Accepted, submission.StatusCode);
        Assert.False(submission.Replayed);
        Assert.NotEqual(Guid.Empty, submission.EventId);
        Assert.NotEqual(Guid.Empty, submission.DeliveryId);
        Assert.Equal("Queued", submission.State);

        await using var database = _database.CreateDbContext();
        var webhookEvent = await database.WebhookEvents.AsNoTracking().SingleAsync();
        var delivery = await database.Deliveries.AsNoTracking().SingleAsync();
        Assert.Equal(submission.EventId, webhookEvent.Id);
        Assert.Equal(submission.DeliveryId, delivery.Id);
        Assert.Equal(webhookEvent.Id, delivery.EventId);
        Assert.Equal(endpoint.Id, webhookEvent.EndpointId);
        Assert.Equal(endpoint.Id, delivery.EndpointId);
    }

    [Fact]
    public async Task IdenticalIdempotentReplayReturnsTheSameIdentifiersAndReplayHeader()
    {
        var endpoint = await CreateEndpointAsync();
        var idempotencyKey = $"event:{Guid.NewGuid():N}";
        var payloadValue = $"payload-{Guid.NewGuid():N}";

        var first = await SubmitEventAsync(endpoint.Id, idempotencyKey, payloadValue);
        var replay = await SubmitEventAsync(endpoint.Id, idempotencyKey, payloadValue);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.EventId, replay.EventId);
        Assert.Equal(first.DeliveryId, replay.DeliveryId);
        Assert.Equal(first.CorrelationId, replay.CorrelationId);

        await using var database = _database.CreateDbContext();
        Assert.Equal(1, await database.WebhookEvents.CountAsync());
        Assert.Equal(1, await database.Deliveries.CountAsync());
    }

    [Fact]
    public async Task ChangedPayloadWithTheSameIdempotencyKeyReturnsConflict()
    {
        var endpoint = await CreateEndpointAsync();
        var idempotencyKey = $"event:{Guid.NewGuid():N}";

        var first = await SubmitEventAsync(
            endpoint.Id,
            idempotencyKey,
            $"payload-{Guid.NewGuid():N}");
        var conflict = await SubmitEventAsync(
            endpoint.Id,
            idempotencyKey,
            $"changed-{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.False(conflict.Replayed);

        await using var database = _database.CreateDbContext();
        Assert.Equal(1, await database.WebhookEvents.CountAsync());
        Assert.Equal(1, await database.Deliveries.CountAsync());
    }

    [Fact]
    public async Task ConcurrentIdenticalRequestsCreateOneEventAndDelivery()
    {
        var endpoint = await CreateEndpointAsync();
        var idempotencyKey = $"event:{Guid.NewGuid():N}";
        var payloadValue = $"payload-{Guid.NewGuid():N}";

        var submissions = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => SubmitEventAsync(endpoint.Id, idempotencyKey, payloadValue)));

        Assert.All(
            submissions,
            submission => Assert.Equal(HttpStatusCode.Accepted, submission.StatusCode));
        Assert.Single(submissions.Select(submission => submission.EventId).Distinct());
        Assert.Single(submissions.Select(submission => submission.DeliveryId).Distinct());
        Assert.Equal(1, submissions.Count(submission => !submission.Replayed));
        Assert.Equal(7, submissions.Count(submission => submission.Replayed));

        await using var database = _database.CreateDbContext();
        Assert.Equal(1, await database.WebhookEvents.CountAsync());
        Assert.Equal(1, await database.Deliveries.CountAsync());
    }

    [Fact]
    public async Task DeliveryHistoryAndDetailExposeOnlySanitizedFields()
    {
        var endpoint = await CreateEndpointAsync();
        var payloadMarker = $"private-payload-{Guid.NewGuid():N}";
        var submission = await SubmitEventAsync(
            endpoint.Id,
            $"event:{Guid.NewGuid():N}",
            payloadMarker);
        Assert.Equal(HttpStatusCode.Accepted, submission.StatusCode);

        var claimToken = Guid.NewGuid();
        var startedAtUtc = TestUtcNow.AddSeconds(1);
        var completedAtUtc = startedAtUtc.AddMilliseconds(25);
        await using (var database = _database.CreateDbContext())
        {
            var delivery = await database.Deliveries.SingleAsync();
            delivery.Claim(claimToken, startedAtUtc);
            delivery.MarkSucceeded(claimToken, completedAtUtc);
            var attempt = new DeliveryAttempt(
                Guid.NewGuid(),
                delivery.Id,
                attemptNumber: 1,
                startedAtUtc);
            attempt.MarkSucceeded(204, completedAtUtc, 25);
            database.DeliveryAttempts.Add(attempt);
            await database.SaveChangesAsync();
        }

        using var historyResponse = await _client.GetAsync("/api/deliveries?limit=20");
        var historyJson = await historyResponse.Content.ReadAsStringAsync();
        using var detailResponse = await _client.GetAsync($"/api/deliveries/{submission.DeliveryId:D}");
        var detailJson = await detailResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        AssertSanitized(historyJson, endpoint.SigningSecret, payloadMarker, claimToken);
        AssertSanitized(detailJson, endpoint.SigningSecret, payloadMarker, claimToken);

        using var historyDocument = JsonDocument.Parse(historyJson);
        var historyItem = Assert.Single(historyDocument.RootElement.EnumerateArray());
        Assert.Equal(submission.DeliveryId, historyItem.GetProperty("id").GetGuid());
        Assert.Equal("Succeeded", historyItem.GetProperty("state").GetString());

        using var detailDocument = JsonDocument.Parse(detailJson);
        var detail = detailDocument.RootElement;
        Assert.Equal(submission.DeliveryId, detail.GetProperty("id").GetGuid());
        Assert.Equal("Succeeded", detail.GetProperty("state").GetString());
        var attemptElement = Assert.Single(detail.GetProperty("attempts").EnumerateArray());
        Assert.Equal("Succeeded", attemptElement.GetProperty("state").GetString());
        Assert.Equal(204, attemptElement.GetProperty("httpStatusCode").GetInt32());
    }

    private async Task<CreatedEndpoint> CreateEndpointAsync()
    {
        var receiverId = Guid.NewGuid();
        var signingSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var url = $"http://receiver.test:8080/webhooks/{receiverId:D}";
        using var response = await _client.PostAsJsonAsync(
            "/api/endpoints",
            new
            {
                name = $"Receiver {Guid.NewGuid():N}",
                url,
                signingSecret,
            });
        var rawResponse = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(rawResponse);
        return new CreatedEndpoint(
            document.RootElement.GetProperty("id").GetGuid(),
            url,
            signingSecret,
            rawResponse);
    }

    private async Task<EventSubmission> SubmitEventAsync(
        Guid endpointId,
        string idempotencyKey,
        string payloadValue)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/events")
        {
            Content = JsonContent.Create(new
            {
                endpointId,
                type = "demo.created",
                payload = new
                {
                    value = payloadValue,
                    sequence = 1,
                },
            }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await _client.SendAsync(request);
        var rawResponse = await response.Content.ReadAsStringAsync();
        var replayed = response.Headers.TryGetValues("Idempotency-Replayed", out var values)
            && values.Single() == "true";

        if (response.StatusCode is not HttpStatusCode.Accepted)
        {
            return new EventSubmission(
                response.StatusCode,
                null,
                null,
                null,
                null,
                replayed,
                rawResponse);
        }

        using var document = JsonDocument.Parse(rawResponse);
        return new EventSubmission(
            response.StatusCode,
            document.RootElement.GetProperty("eventId").GetGuid(),
            document.RootElement.GetProperty("deliveryId").GetGuid(),
            document.RootElement.GetProperty("state").GetString(),
            document.RootElement.GetProperty("correlationId").GetString(),
            replayed,
            rawResponse);
    }

    private static string[] GetPropertyNames(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AssertSanitized(
        string json,
        string signingSecret,
        string payloadMarker,
        Guid claimToken)
    {
        foreach (var forbiddenValue in new[]
        {
            signingSecret,
            payloadMarker,
            claimToken.ToString("D"),
            "protectedSigningSecret",
            "payloadJson",
            "envelopeJson",
            "envelopeHash",
            "claimToken",
            "targetUrl",
        })
        {
            Assert.False(json.Contains(forbiddenValue, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed record CreatedEndpoint(
        Guid Id,
        string Url,
        string SigningSecret,
        string RawResponse);

    private sealed record EventSubmission(
        HttpStatusCode StatusCode,
        Guid? EventId,
        Guid? DeliveryId,
        string? State,
        string? CorrelationId,
        bool Replayed,
        string RawResponse);
}
