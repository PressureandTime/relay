namespace Relay.Worker;

public static class WorkerServiceExtensions
{
    public const string DeliveryClientName = "relay-delivery";

    public static IServiceCollection AddRelayDeliveryWorker(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<DeliveryProcessor>();
        services.AddHttpClient(DeliveryClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            MaxConnectionsPerServer = 4,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        });
        services.AddHostedService<DeliveryWorker>();

        return services;
    }
}
