using Microsoft.EntityFrameworkCore;
using Relay.Core;
using Relay.Infrastructure;

namespace Relay.Worker;

public sealed class DeliveryRetentionCleaner(RelayDbContext database)
{
    private const int BatchSize = 100;

    public async Task<int> CleanupAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        var eventIds = await EligibleEventGroups(cutoffUtc)
            .OrderBy(webhookEvent => webhookEvent.CreatedAtUtc)
            .ThenBy(webhookEvent => webhookEvent.Id)
            .Select(webhookEvent => webhookEvent.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (eventIds.Count == 0)
        {
            return 0;
        }

        return await EligibleEventGroups(cutoffUtc)
            .Where(webhookEvent => eventIds.Contains(webhookEvent.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private IQueryable<WebhookEvent> EligibleEventGroups(DateTimeOffset cutoffUtc) =>
        database.WebhookEvents
            .AsNoTracking()
            .Where(webhookEvent => webhookEvent.CreatedAtUtc < cutoffUtc)
            .Where(webhookEvent => database.Deliveries.Any(
                delivery => delivery.EventId == webhookEvent.Id))
            .Where(webhookEvent => !database.Deliveries.Any(delivery =>
                delivery.EventId == webhookEvent.Id
                && ((delivery.State != DeliveryState.Succeeded
                        && delivery.State != DeliveryState.Failed)
                    || delivery.CompletedAtUtc == null
                    || delivery.CompletedAtUtc >= cutoffUtc)));
}
