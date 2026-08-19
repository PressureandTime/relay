namespace Relay.Worker;

public static class WorkerServiceExtensions
{
    public const string DeliveryClientName = "relay-delivery";

    public static IServiceCollection AddRelayDeliveryWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<DeliveryRetentionOptions>()
            .Bind(configuration.GetSection(DeliveryRetentionOptions.SectionName))
            .Validate(
                options => options.RetainFor > TimeSpan.Zero
                    && options.RetainFor <= TimeSpan.FromDays(36_500),
                "Relay delivery retention must be greater than zero and no more than 100 years.")
            .Validate(
                options => options.CleanupInterval >= TimeSpan.FromMinutes(1)
                    && options.CleanupInterval <= TimeSpan.FromDays(30),
                "Relay delivery cleanup interval must be between one minute and 30 days.")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<DeliveryProcessor>();
        services.AddScoped<DeliveryRetentionCleaner>();
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
