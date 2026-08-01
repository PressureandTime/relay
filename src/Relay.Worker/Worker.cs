namespace Relay.Worker;

public sealed partial class DeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogReady(logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
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
}
