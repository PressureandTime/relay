extern alias RelayWorker;

using System.Security.Cryptography;
using System.Text;
using DeliveryRetentionCleaner = RelayWorker::Relay.Worker.DeliveryRetentionCleaner;
using Microsoft.EntityFrameworkCore;
using Relay.Core;
using Relay.Infrastructure;

namespace Relay.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class DeliveryRetentionIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset CutoffUtc =
        new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlFixture _database;

    public DeliveryRetentionIntegrationTests(PostgreSqlFixture database)
    {
        _database = database;
    }

    public Task InitializeAsync() => _database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CleanupRemovesOnlyExpiredCompletedEventGroups()
    {
        var endpoint = await CreateEndpointAsync();
        var expired = await SeedGroupAsync(
            endpoint.Id,
            new DeliverySeed(DeliveryState.Failed, CutoffUtc.AddDays(-2)),
            new DeliverySeed(DeliveryState.Succeeded, CutoffUtc.AddDays(-1)));
        var atCutoff = await SeedGroupAsync(
            endpoint.Id,
            new DeliverySeed(DeliveryState.Succeeded, CutoffUtc));
        var recent = await SeedGroupAsync(
            endpoint.Id,
            new DeliverySeed(DeliveryState.Failed, CutoffUtc.AddMinutes(1)));
        var queued = await SeedGroupAsync(
            endpoint.Id,
            new DeliverySeed(DeliveryState.Queued, null));
        var processing = await SeedGroupAsync(
            endpoint.Id,
            new DeliverySeed(DeliveryState.Processing, null));
        var retryScheduled = await SeedGroupAsync(
            endpoint.Id,
            new DeliverySeed(DeliveryState.RetryScheduled, null));
        var mixedLineage = await SeedGroupAsync(
            endpoint.Id,
            new DeliverySeed(DeliveryState.Failed, CutoffUtc.AddDays(-2)),
            new DeliverySeed(DeliveryState.Succeeded, CutoffUtc.AddMinutes(1)));

        await using (var cleanupDatabase = _database.CreateDbContext())
        {
            var cleaner = new DeliveryRetentionCleaner(cleanupDatabase);
            Assert.Equal(
                1,
                await cleaner.CleanupAsync(CutoffUtc, CancellationToken.None));
        }

        await using var verification = _database.CreateDbContext();
        Assert.DoesNotContain(
            expired.EventId,
            await verification.WebhookEvents.Select(value => value.Id).ToListAsync());
        Assert.Equal(
            0,
            await verification.Deliveries.CountAsync(
                delivery => expired.DeliveryIds.Contains(delivery.Id)));
        Assert.Equal(
            0,
            await verification.DeliveryAttempts.CountAsync(
                attempt => expired.DeliveryIds.Contains(attempt.DeliveryId)));

        var preservedEventIds = new[]
        {
            atCutoff.EventId,
            recent.EventId,
            queued.EventId,
            processing.EventId,
            retryScheduled.EventId,
            mixedLineage.EventId,
        };
        Assert.Equal(
            preservedEventIds.Order(),
            (await verification.WebhookEvents
                .Where(value => preservedEventIds.Contains(value.Id))
                .Select(value => value.Id)
                .ToListAsync())
                .Order());
        Assert.Equal(
            mixedLineage.DeliveryIds.Count,
            await verification.Deliveries.CountAsync(
                delivery => mixedLineage.DeliveryIds.Contains(delivery.Id)));
        Assert.True(await verification.WebhookEndpoints.AnyAsync(
            candidate => candidate.Id == endpoint.Id));
    }

    [Fact]
    public async Task CleanupLimitsEachPassToOneHundredEventGroups()
    {
        var endpoint = await CreateEndpointAsync();
        await using (var setup = _database.CreateDbContext())
        {
            for (var index = 0; index < 101; index++)
            {
                var group = CreateGroup(
                    endpoint.Id,
                    new DeliverySeed(DeliveryState.Succeeded, CutoffUtc.AddDays(-1)));
                setup.WebhookEvents.Add(group.Event);
                setup.Deliveries.AddRange(group.Deliveries);
                setup.DeliveryAttempts.AddRange(group.Attempts);
            }

            await setup.SaveChangesAsync();
        }

        await using (var firstDatabase = _database.CreateDbContext())
        {
            var cleaner = new DeliveryRetentionCleaner(firstDatabase);
            Assert.Equal(
                100,
                await cleaner.CleanupAsync(CutoffUtc, CancellationToken.None));
        }

        await using (var secondDatabase = _database.CreateDbContext())
        {
            var cleaner = new DeliveryRetentionCleaner(secondDatabase);
            Assert.Equal(
                1,
                await cleaner.CleanupAsync(CutoffUtc, CancellationToken.None));
        }

        await using var verification = _database.CreateDbContext();
        Assert.Empty(await verification.WebhookEvents.ToListAsync());
        Assert.Empty(await verification.Deliveries.ToListAsync());
        Assert.Empty(await verification.DeliveryAttempts.ToListAsync());
        Assert.Single(await verification.WebhookEndpoints.ToListAsync());
    }

    private async Task<WebhookEndpoint> CreateEndpointAsync()
    {
        var endpoint = new WebhookEndpoint(
            Guid.NewGuid(),
            $"Retention receiver {Guid.NewGuid():N}",
            $"http://receiver.test:8080/webhooks/{Guid.NewGuid():D}",
            "protected-synthetic-secret",
            CutoffUtc.AddDays(-60));
        await using var database = _database.CreateDbContext();
        database.WebhookEndpoints.Add(endpoint);
        await database.SaveChangesAsync();
        return endpoint;
    }

    private async Task<SeededGroup> SeedGroupAsync(
        Guid endpointId,
        params DeliverySeed[] deliverySeeds)
    {
        var group = CreateGroup(endpointId, deliverySeeds);
        await using var database = _database.CreateDbContext();
        database.WebhookEvents.Add(group.Event);
        database.Deliveries.AddRange(group.Deliveries);
        database.DeliveryAttempts.AddRange(group.Attempts);
        await database.SaveChangesAsync();
        return new SeededGroup(
            group.Event.Id,
            group.Deliveries.Select(delivery => delivery.Id).ToArray());
    }

    private static EventGroup CreateGroup(
        Guid endpointId,
        params DeliverySeed[] deliverySeeds)
    {
        var eventId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");
        var webhookEvent = new WebhookEvent(
            eventId,
            endpointId,
            "demo.retention",
            "{\"value\":1}",
            $"event:{Guid.NewGuid():N}",
            Convert.ToHexStringLower(SHA256.HashData(RandomNumberGenerator.GetBytes(32))),
            correlationId,
            CutoffUtc.AddDays(-40));
        var deliveries = new List<Delivery>();
        var attempts = new List<DeliveryAttempt>();

        foreach (var deliverySeed in deliverySeeds)
        {
            var deliveryId = Guid.NewGuid();
            var envelopeJson =
                $"{{\"eventId\":\"{eventId:D}\",\"deliveryId\":\"{deliveryId:D}\",\"type\":\"demo.retention\",\"payload\":{{\"value\":1}}}}";
            var delivery = deliveries.Count == 0
                ? new Delivery(
                    deliveryId,
                    eventId,
                    endpointId,
                    envelopeJson,
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(envelopeJson))),
                    correlationId,
                    CutoffUtc.AddDays(-39))
                : new Delivery(
                    deliveryId,
                    eventId,
                    endpointId,
                    envelopeJson,
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(envelopeJson))),
                    Guid.NewGuid().ToString("N"),
                    deliveries[0].Id,
                    $"replay:{Guid.NewGuid():N}",
                    CutoffUtc.AddDays(-38));

            MoveToState(delivery, deliverySeed, attempts);
            deliveries.Add(delivery);
        }

        return new EventGroup(webhookEvent, deliveries, attempts);
    }

    private static void MoveToState(
        Delivery delivery,
        DeliverySeed seed,
        List<DeliveryAttempt> attempts)
    {
        if (seed.State == DeliveryState.Queued)
        {
            return;
        }

        var completedAtUtc = seed.CompletedAtUtc ?? CutoffUtc.AddDays(-1);
        var startedAtUtc = completedAtUtc.AddSeconds(-1);
        var claimToken = Guid.NewGuid();
        delivery.Claim(claimToken, startedAtUtc);
        var attempt = new DeliveryAttempt(
            Guid.NewGuid(),
            delivery.Id,
            delivery.AttemptCount,
            startedAtUtc);
        attempts.Add(attempt);

        switch (seed.State)
        {
            case DeliveryState.Processing:
                return;
            case DeliveryState.RetryScheduled:
                attempt.MarkFailed(
                    503,
                    "http_status",
                    "The receiver returned HTTP 503.",
                    completedAtUtc,
                    1000);
                delivery.ScheduleRetry(completedAtUtc.AddMinutes(1));
                return;
            case DeliveryState.Succeeded:
                attempt.MarkSucceeded(204, completedAtUtc, 1000);
                delivery.MarkSucceeded(claimToken, completedAtUtc);
                return;
            case DeliveryState.Failed:
                attempt.MarkFailed(
                    400,
                    "http_status",
                    "The receiver returned HTTP 400.",
                    completedAtUtc,
                    1000);
                delivery.MarkFailed(
                    claimToken,
                    "http_status",
                    "The receiver returned HTTP 400.",
                    completedAtUtc);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(seed), seed.State, null);
        }
    }

    private sealed record DeliverySeed(
        DeliveryState State,
        DateTimeOffset? CompletedAtUtc);

    private sealed record EventGroup(
        WebhookEvent Event,
        IReadOnlyList<Delivery> Deliveries,
        IReadOnlyList<DeliveryAttempt> Attempts);

    private sealed record SeededGroup(
        Guid EventId,
        IReadOnlyList<Guid> DeliveryIds);
}
