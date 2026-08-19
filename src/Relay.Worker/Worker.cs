using Microsoft.Extensions.Options;

namespace Relay.Worker;

public sealed partial class DeliveryWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<DeliveryRetentionOptions> retentionOptions,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogReady(logger);
        var retention = retentionOptions.Value;
        DateTimeOffset? nextCleanupAtUtc = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var now = timeProvider.GetUtcNow();
                if (retention.Enabled
                    && (nextCleanupAtUtc is null || now >= nextCleanupAtUtc))
                {
                    nextCleanupAtUtc = now + retention.CleanupInterval;
                    var cleaner = scope.ServiceProvider
                        .GetRequiredService<DeliveryRetentionCleaner>();
                    var cutoffUtc = now - retention.RetainFor;
                    var deletedEventCount = await cleaner.CleanupAsync(
                        cutoffUtc,
                        stoppingToken);
                    if (deletedEventCount > 0)
                    {
                        LogRetentionCleanup(logger, deletedEventCount, cutoffUtc);
                    }
                }

                var processor = scope.ServiceProvider.GetRequiredService<DeliveryProcessor>();
                if (!await processor.TryProcessNextAsync(stoppingToken))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogProcessingFailure(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "Relay delivery worker is ready")]
    private static partial void LogReady(ILogger logger);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Error,
        Message = "Unexpected delivery worker failure")]
    private static partial void LogProcessingFailure(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Removed {EventCount} expired webhook event groups completed before {CutoffUtc}")]
    private static partial void LogRetentionCleanup(
        ILogger logger,
        int eventCount,
        DateTimeOffset cutoffUtc);
}
