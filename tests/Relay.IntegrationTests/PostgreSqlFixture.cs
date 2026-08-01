using Microsoft.EntityFrameworkCore;
using Relay.Infrastructure;
using Testcontainers.PostgreSql;

namespace Relay.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlTestGroup : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL integration";
}

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;

    public PostgreSqlFixture()
    {
        _container = new PostgreSqlBuilder("postgres:18.4")
            .WithDatabase($"relay_{Guid.NewGuid():N}")
            .WithUsername("relay")
            .WithPassword($"relay-{Guid.NewGuid():N}")
            .Build();
    }

    public string ConnectionString { get; private set; } = string.Empty;

    public int AppliedMigrationCountBeforeMigration { get; private set; }

    public int AvailableMigrationCount { get; private set; }

    public int AppliedMigrationCountAfterMigration { get; private set; }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        await using var database = CreateDbContext();
        AppliedMigrationCountBeforeMigration =
            (await database.Database.GetAppliedMigrationsAsync()).Count();
        AvailableMigrationCount = database.Database.GetMigrations().Count();

        await database.Database.MigrateAsync();

        AppliedMigrationCountAfterMigration =
            (await database.Database.GetAppliedMigrationsAsync()).Count();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public RelayDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<RelayDbContext>()
            .UseNpgsql(
                ConnectionString,
                npgsql => npgsql.MigrationsAssembly(typeof(RelayDbContext).Assembly.FullName))
            .Options;
        return new RelayDbContext(options);
    }

    public async Task ResetAsync()
    {
        await using var database = CreateDbContext();
        await database.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE delivery_attempts, deliveries, webhook_events, webhook_endpoints;");
    }
}
