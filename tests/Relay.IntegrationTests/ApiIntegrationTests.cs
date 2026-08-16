using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Relay.Core;
using Relay.Infrastructure;
using Testcontainers.PostgreSql;

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
        Assert.Equal(5, _database.AvailableMigrationCount);
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
    public async Task BackfillMigrationRepairsAlreadyUpgradedInFlightDeliveryAndAttempt()
    {
        await using var container = new PostgreSqlBuilder("postgres:18.4")
            .WithDatabase($"relay_upgrade_{Guid.NewGuid():N}")
            .WithUsername("relay")
            .WithPassword($"relay-{Guid.NewGuid():N}")
            .Build();
        await container.StartAsync();

        var options = new DbContextOptionsBuilder<RelayDbContext>()
            .UseNpgsql(
                container.GetConnectionString(),
                npgsql => npgsql.MigrationsAssembly(typeof(RelayDbContext).Assembly.FullName))
            .Options;
        await using var database = new RelayDbContext(options);
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260801221349_AddRetryAndReplay");

        var endpointId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var claimToken = Guid.NewGuid();
        var payloadJson = "{\"value\":1}";
        var envelopeJson =
            $"{{\"eventId\":\"{eventId:D}\",\"deliveryId\":\"{deliveryId:D}\",\"type\":\"demo.migration\",\"payload\":{{\"value\":1}}}}";

        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO webhook_endpoints
                ("Id", "Name", "TargetUrl", "ProtectedSigningSecret", "CreatedAtUtc")
            VALUES
                ({endpointId}, 'Migration receiver', 'http://receiver.test:8080/webhooks/11111111-1111-1111-1111-111111111111', 'protected', {TestUtcNow});

            INSERT INTO webhook_events
                ("Id", "EndpointId", "EventType", "PayloadJson", "IdempotencyKey", "RequestFingerprint", "CorrelationId", "CreatedAtUtc")
            VALUES
                ({eventId}, {endpointId}, 'demo.migration', CAST({payloadJson} AS jsonb), 'migration-event', {new string('a', 64)}, 'migration-correlation', {TestUtcNow});

            INSERT INTO deliveries
                ("Id", "EventId", "EndpointId", "State", "EnvelopeJson", "EnvelopeHash", "ClaimToken", "CorrelationId", "CreatedAtUtc", "StartedAtUtc")
            VALUES
                ({deliveryId}, {eventId}, {endpointId}, 'Processing', {envelopeJson}, {new string('b', 64)}, {claimToken}, 'migration-correlation', {TestUtcNow}, {TestUtcNow});

            INSERT INTO delivery_attempts
                ("Id", "DeliveryId", "AttemptNumber", "State", "StartedAtUtc")
            VALUES
                ({attemptId}, {deliveryId}, 1, 'Processing', {TestUtcNow});
            """);

        await migrator.MigrateAsync();
        database.ChangeTracker.Clear();

        var delivery = await database.Deliveries.AsNoTracking().SingleAsync();
        var attempt = await database.DeliveryAttempts.AsNoTracking().SingleAsync();
        var endpoint = await database.WebhookEndpoints.AsNoTracking().SingleAsync();
        Assert.Equal(EndpointState.Active, endpoint.State);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(DeliveryState.Failed, delivery.State);
        Assert.Null(delivery.ClaimToken);
        Assert.Equal("migration_backfill", delivery.ErrorCode);
        Assert.NotNull(delivery.CompletedAtUtc);
        Assert.Equal(AttemptState.Failed, attempt.State);
        Assert.Equal("migration_backfill", attempt.ErrorCode);
        Assert.NotNull(attempt.CompletedAtUtc);
        Assert.NotNull(attempt.DurationMilliseconds);
    }

    [Fact]
    public async Task CreatingAndListingEndpointsProtectsAndNeverReturnsTheSecret()
    {
        var endpoint = await CreateEndpointAsync();

        Assert.False(endpoint.RawResponse.Contains(endpoint.SigningSecret, StringComparison.Ordinal));
        Assert.False(endpoint.RawResponse.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            ["createdAtUtc", "id", "name", "state", "url"],
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
        Assert.Equal("Active", listedEndpoint.GetProperty("state").GetString());
        Assert.Equal(
            ["createdAtUtc", "id", "name", "state", "url"],
            listedEndpoint.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task EndpointCanBeDisabledAndReactivatedIdempotently()
    {
        var endpoint = await CreateEndpointAsync();

        var firstDisable = await SetEndpointStateAsync(endpoint.Id, "disable");
        var secondDisable = await SetEndpointStateAsync(endpoint.Id, "disable");
        var firstReactivate = await SetEndpointStateAsync(endpoint.Id, "reactivate");
        var secondReactivate = await SetEndpointStateAsync(endpoint.Id, "reactivate");
        var missing = await SetEndpointStateAsync(Guid.NewGuid(), "disable");

        Assert.Equal(HttpStatusCode.OK, firstDisable.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondDisable.StatusCode);
        Assert.Equal("Disabled", firstDisable.State);
        Assert.Equal("Disabled", secondDisable.State);
        Assert.Equal(HttpStatusCode.OK, firstReactivate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondReactivate.StatusCode);
        Assert.Equal("Active", firstReactivate.State);
        Assert.Equal("Active", secondReactivate.State);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await using var database = _database.CreateDbContext();
        var storedEndpoint = await database.WebhookEndpoints.AsNoTracking().SingleAsync();
        Assert.Equal(EndpointState.Active, storedEndpoint.State);
    }

    [Fact]
    public async Task DisabledEndpointRejectsNewEventsButPreservesIdempotentResult()
    {
        var endpoint = await CreateEndpointAsync();
        var idempotencyKey = $"event:{Guid.NewGuid():N}";
        var payload = $"payload-{Guid.NewGuid():N}";
        var accepted = await SubmitEventAsync(endpoint.Id, idempotencyKey, payload);
        await SetEndpointStateAsync(endpoint.Id, "disable");

        var repeated = await SubmitEventAsync(endpoint.Id, idempotencyKey, payload);
        var rejected = await SubmitEventAsync(
            endpoint.Id,
            $"event:{Guid.NewGuid():N}",
            $"payload-{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.True(repeated.Replayed);
        Assert.Equal(accepted.EventId, repeated.EventId);
        Assert.Equal(accepted.DeliveryId, repeated.DeliveryId);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains("Webhook endpoint is disabled.", rejected.RawResponse, StringComparison.Ordinal);

        await using var database = _database.CreateDbContext();
        Assert.Equal(1, await database.WebhookEvents.CountAsync());
        Assert.Equal(1, await database.Deliveries.CountAsync());
    }

    [Fact]
    public async Task DisabledEndpointRejectsNewReplaysButPreservesIdempotentResult()
    {
        var endpoint = await CreateEndpointAsync();
        var original = await SubmitEventAsync(
            endpoint.Id,
            $"event:{Guid.NewGuid():N}",
            $"payload-{Guid.NewGuid():N}");
        await MarkDeliveryFailedAsync(original.DeliveryId!.Value);
        var idempotencyKey = $"replay:{Guid.NewGuid():N}";
        var accepted = await ReplayDeliveryAsync(original.DeliveryId.Value, idempotencyKey);
        await SetEndpointStateAsync(endpoint.Id, "disable");

        var repeated = await ReplayDeliveryAsync(original.DeliveryId.Value, idempotencyKey);
        var rejected = await ReplayDeliveryAsync(
            original.DeliveryId.Value,
            $"replay:{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.True(repeated.Replayed);
        Assert.Equal(accepted.DeliveryId, repeated.DeliveryId);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains("Webhook endpoint is disabled.", rejected.RawResponse, StringComparison.Ordinal);

        await using var database = _database.CreateDbContext();
        Assert.Equal(2, await database.Deliveries.CountAsync());
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
    public async Task ReplayingFailedDeliveryIsIdempotentAndPreservesEvent()
    {
        var endpoint = await CreateEndpointAsync();
        var original = await SubmitEventAsync(
            endpoint.Id,
            $"event:{Guid.NewGuid():N}",
            $"payload-{Guid.NewGuid():N}");
        await MarkDeliveryFailedAsync(original.DeliveryId!.Value);
        var idempotencyKey = $"replay:{Guid.NewGuid():N}";

        var first = await ReplayDeliveryAsync(original.DeliveryId.Value, idempotencyKey);
        var repeated = await ReplayDeliveryAsync(original.DeliveryId.Value, idempotencyKey);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.False(first.Replayed);
        Assert.True(repeated.Replayed);
        Assert.Equal(original.DeliveryId, first.OriginalDeliveryId);
        Assert.Equal(first.DeliveryId, repeated.DeliveryId);
        Assert.Equal(first.CorrelationId, repeated.CorrelationId);
        Assert.NotEqual(original.CorrelationId, first.CorrelationId);

        await using var database = _database.CreateDbContext();
        var deliveries = await database.Deliveries
            .AsNoTracking()
            .OrderBy(delivery => delivery.CreatedAtUtc)
            .ToListAsync();
        Assert.Equal(2, deliveries.Count);
        var replay = deliveries[1];
        Assert.Equal(original.EventId, replay.EventId);
        Assert.Equal(original.DeliveryId, replay.ReplayOfDeliveryId);
        Assert.Equal(idempotencyKey, replay.ReplayIdempotencyKey);
        Assert.Equal(DeliveryState.Queued, replay.State);
        using var envelope = JsonDocument.Parse(replay.EnvelopeJson);
        Assert.Equal(replay.Id, envelope.RootElement.GetProperty("deliveryId").GetGuid());
        Assert.Equal(original.EventId, envelope.RootElement.GetProperty("eventId").GetGuid());
    }

    [Fact]
    public async Task ConcurrentReplayRequestsCreateOneReplayDelivery()
    {
        var endpoint = await CreateEndpointAsync();
        var original = await SubmitEventAsync(
            endpoint.Id,
            $"event:{Guid.NewGuid():N}",
            $"payload-{Guid.NewGuid():N}");
        await MarkDeliveryFailedAsync(original.DeliveryId!.Value);
        var idempotencyKey = $"replay:{Guid.NewGuid():N}";

        var submissions = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => ReplayDeliveryAsync(original.DeliveryId.Value, idempotencyKey)));

        Assert.All(
            submissions,
            submission => Assert.Equal(HttpStatusCode.Accepted, submission.StatusCode));
        Assert.Single(submissions.Select(submission => submission.DeliveryId).Distinct());
        Assert.Equal(1, submissions.Count(submission => !submission.Replayed));
        Assert.Equal(7, submissions.Count(submission => submission.Replayed));

        await using var database = _database.CreateDbContext();
        Assert.Equal(2, await database.Deliveries.CountAsync());
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
        var historyItem = Assert.Single(
            historyDocument.RootElement.GetProperty("items").EnumerateArray());
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

    [Fact]
    public async Task DeliveryHistoryFiltersByStateEndpointAndEventType()
    {
        var firstEndpoint = await CreateEndpointAsync();
        var secondEndpoint = await CreateEndpointAsync();
        var failed = await SubmitEventAsync(
            firstEndpoint.Id,
            $"event:{Guid.NewGuid():N}",
            $"payload-{Guid.NewGuid():N}",
            "demo.failed");
        var wrongState = await SubmitEventAsync(
            firstEndpoint.Id,
            $"event:{Guid.NewGuid():N}",
            $"payload-{Guid.NewGuid():N}",
            "demo.failed");
        var wrongEndpoint = await SubmitEventAsync(
            secondEndpoint.Id,
            $"event:{Guid.NewGuid():N}",
            $"payload-{Guid.NewGuid():N}",
            "demo.failed");
        var wrongEventType = await SubmitEventAsync(
            firstEndpoint.Id,
            $"event:{Guid.NewGuid():N}",
            $"payload-{Guid.NewGuid():N}",
            "demo.other");
        await MarkDeliveryFailedAsync(failed.DeliveryId!.Value);
        await MarkDeliveryFailedAsync(wrongEndpoint.DeliveryId!.Value);
        await MarkDeliveryFailedAsync(wrongEventType.DeliveryId!.Value);

        Assert.NotNull(wrongState.DeliveryId);

        using var response = await _client.GetAsync(
            $"/api/deliveries?state=failed&endpointId={firstEndpoint.Id:D}&eventType=demo.failed&limit=20");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single(
            document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(failed.DeliveryId, item.GetProperty("id").GetGuid());
        Assert.Equal(firstEndpoint.Id, item.GetProperty("endpointId").GetGuid());
        Assert.Equal("demo.failed", item.GetProperty("eventType").GetString());
        Assert.Equal("Failed", item.GetProperty("state").GetString());
    }

    [Fact]
    public async Task DeliveryHistoryUsesStableFilterBoundKeysetPagination()
    {
        var endpoint = await CreateEndpointAsync();
        var createdAtUtc = TestUtcNow.AddMinutes(10);
        var originalIds = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            originalIds.Add(await SeedQueuedDeliveryAsync(
                endpoint.Id,
                createdAtUtc,
                "demo.page"));
        }

        var expectedOrder = originalIds.OrderDescending().ToArray();
        var first = await GetDeliveryPageAsync("eventType=demo.page&limit=2");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(expectedOrder[..2], first.DeliveryIds);
        Assert.NotNull(first.NextCursor);

        var newerId = await SeedQueuedDeliveryAsync(
            endpoint.Id,
            createdAtUtc.AddMinutes(1),
            "demo.page");
        var second = await GetDeliveryPageAsync(
            $"eventType=demo.page&limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        var third = await GetDeliveryPageAsync(
            $"eventType=demo.page&limit=2&cursor={Uri.EscapeDataString(second.NextCursor!)}");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(expectedOrder[2..4], second.DeliveryIds);
        Assert.Equal(expectedOrder[4..], third.DeliveryIds);
        Assert.Null(third.NextCursor);
        Assert.Equal(
            expectedOrder,
            first.DeliveryIds.Concat(second.DeliveryIds).Concat(third.DeliveryIds));
        Assert.DoesNotContain(newerId, second.DeliveryIds.Concat(third.DeliveryIds));

        var refreshed = await GetDeliveryPageAsync("eventType=demo.page&limit=2");
        Assert.Equal(newerId, refreshed.DeliveryIds[0]);

        var filterMismatch = await GetDeliveryPageAsync(
            $"eventType=demo.other&limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal(HttpStatusCode.BadRequest, filterMismatch.StatusCode);
        Assert.Contains("cursor", filterMismatch.RawResponse, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("state=not-a-state", "state")]
    [InlineData("endpointId=00000000-0000-0000-0000-000000000000", "endpointId")]
    [InlineData("eventType=invalid%20type", "eventType")]
    [InlineData("cursor=not-a-cursor", "cursor")]
    public async Task DeliveryHistoryRejectsInvalidFilters(
        string query,
        string expectedField)
    {
        using var response = await _client.GetAsync($"/api/deliveries?{query}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty(expectedField, out _));
    }

    [Fact]
    public async Task DeliveryHistoryRejectsInvalidCursorPayloads()
    {
        var payloads = new[]
        {
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 2,
                createdAtUtcTicks = TestUtcNow.UtcDateTime.Ticks,
                id = Guid.NewGuid(),
                state = (string?)null,
                endpointId = (Guid?)null,
                eventType = (string?)null,
            }),
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                createdAtUtcTicks = 0,
                id = Guid.NewGuid(),
                state = (string?)null,
                endpointId = (Guid?)null,
                eventType = (string?)null,
            }),
        };

        foreach (var payload in payloads)
        {
            var cursor = Convert.ToBase64String(payload)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            using var response = await _client.GetAsync($"/api/deliveries?cursor={cursor}");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("cursor", out _));
        }
    }

    private async Task<Guid> SeedQueuedDeliveryAsync(
        Guid endpointId,
        DateTimeOffset createdAtUtc,
        string eventType)
    {
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");
        var payloadJson = "{\"value\":1}";
        var envelopeJson =
            $"{{\"eventId\":\"{eventId:D}\",\"deliveryId\":\"{deliveryId:D}\",\"type\":\"{eventType}\",\"payload\":{{\"value\":1}}}}";
        var webhookEvent = new WebhookEvent(
            eventId,
            endpointId,
            eventType,
            payloadJson,
            $"event:{Guid.NewGuid():N}",
            new string('a', RelayLimits.FingerprintLength),
            correlationId,
            createdAtUtc);
        var delivery = new Delivery(
            deliveryId,
            eventId,
            endpointId,
            envelopeJson,
            new string('b', RelayLimits.EnvelopeHashLength),
            correlationId,
            createdAtUtc);

        await using var database = _database.CreateDbContext();
        database.AddRange(webhookEvent, delivery);
        await database.SaveChangesAsync();
        return deliveryId;
    }

    private async Task<DeliveryPageSubmission> GetDeliveryPageAsync(string query)
    {
        using var response = await _client.GetAsync($"/api/deliveries?{query}");
        var rawResponse = await response.Content.ReadAsStringAsync();
        if (response.StatusCode is not HttpStatusCode.OK)
        {
            return new DeliveryPageSubmission(
                response.StatusCode,
                [],
                null,
                rawResponse);
        }

        using var document = JsonDocument.Parse(rawResponse);
        var deliveryIds = document.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToArray();
        var cursorElement = document.RootElement.GetProperty("nextCursor");
        return new DeliveryPageSubmission(
            response.StatusCode,
            deliveryIds,
            cursorElement.ValueKind == JsonValueKind.Null
                ? null
                : cursorElement.GetString(),
            rawResponse);
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
        string payloadValue,
        string eventType = "demo.created")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/events")
        {
            Content = JsonContent.Create(new
            {
                endpointId,
                type = eventType,
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

    private async Task<EndpointStateSubmission> SetEndpointStateAsync(
        Guid endpointId,
        string action)
    {
        using var response = await _client.PostAsync(
            $"/api/endpoints/{endpointId:D}/{action}",
            content: null);
        var rawResponse = await response.Content.ReadAsStringAsync();
        if (response.StatusCode is not HttpStatusCode.OK)
        {
            return new EndpointStateSubmission(response.StatusCode, null, rawResponse);
        }

        using var document = JsonDocument.Parse(rawResponse);
        return new EndpointStateSubmission(
            response.StatusCode,
            document.RootElement.GetProperty("state").GetString(),
            rawResponse);
    }

    private async Task MarkDeliveryFailedAsync(Guid deliveryId)
    {
        await using var database = _database.CreateDbContext();
        var delivery = await database.Deliveries.SingleAsync(candidate => candidate.Id == deliveryId);
        var claimToken = Guid.NewGuid();
        var startedAtUtc = TestUtcNow.AddSeconds(1);
        var completedAtUtc = TestUtcNow.AddSeconds(2);
        delivery.Claim(claimToken, startedAtUtc);
        delivery.MarkFailed(
            claimToken,
            "http_status",
            "The receiver returned HTTP 400.",
            completedAtUtc);
        var attempt = new DeliveryAttempt(
            Guid.NewGuid(),
            delivery.Id,
            delivery.AttemptCount,
            startedAtUtc);
        attempt.MarkFailed(
            400,
            "http_status",
            "The receiver returned HTTP 400.",
            completedAtUtc,
            1000);
        database.DeliveryAttempts.Add(attempt);
        await database.SaveChangesAsync();
    }

    private async Task<ReplaySubmission> ReplayDeliveryAsync(
        Guid originalDeliveryId,
        string idempotencyKey)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/deliveries/{originalDeliveryId:D}/replays");
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await _client.SendAsync(request);
        var rawResponse = await response.Content.ReadAsStringAsync();
        var replayed = response.Headers.TryGetValues("Idempotency-Replayed", out var values)
            && values.Single() == "true";

        if (response.StatusCode is not HttpStatusCode.Accepted)
        {
            return new ReplaySubmission(
                response.StatusCode,
                null,
                null,
                null,
                null,
                replayed,
                rawResponse);
        }

        using var document = JsonDocument.Parse(rawResponse);
        return new ReplaySubmission(
            response.StatusCode,
            document.RootElement.GetProperty("originalDeliveryId").GetGuid(),
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

    private sealed record EndpointStateSubmission(
        HttpStatusCode StatusCode,
        string? State,
        string RawResponse);

    private sealed record DeliveryPageSubmission(
        HttpStatusCode StatusCode,
        Guid[] DeliveryIds,
        string? NextCursor,
        string RawResponse);

    private sealed record ReplaySubmission(
        HttpStatusCode StatusCode,
        Guid? OriginalDeliveryId,
        Guid? DeliveryId,
        string? State,
        string? CorrelationId,
        bool Replayed,
        string RawResponse);
}
