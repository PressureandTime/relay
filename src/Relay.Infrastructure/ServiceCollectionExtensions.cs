using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Relay.Core;

namespace Relay.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRelayPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Relay")
            ?? throw new InvalidOperationException(
                "The ConnectionStrings:Relay configuration value is required.");

        services.AddDbContextPool<RelayDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(RelayDbContext).Assembly.FullName)));

        return services;
    }

    public static IServiceCollection AddRelayDataProtection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var keyPath = configuration["Relay:DataProtectionKeyPath"]
            ?? Path.Combine(AppContext.BaseDirectory, ".relay-data", "keys");
        var keyDirectory = Directory.CreateDirectory(keyPath);

        services.AddDataProtection()
            .SetApplicationName("Relay")
            .PersistKeysToFileSystem(keyDirectory);
        services.AddSingleton<IEndpointSecretProtector, EndpointSecretProtector>();

        return services;
    }

    public static IServiceCollection AddRelayTargetPolicy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var receiverBaseUrl = configuration["Relay:ReceiverBaseUrl"]
            ?? throw new InvalidOperationException(
                "The Relay:ReceiverBaseUrl configuration value is required.");
        services.AddSingleton(new DemoWebhookTargetPolicy(receiverBaseUrl));

        return services;
    }
}
