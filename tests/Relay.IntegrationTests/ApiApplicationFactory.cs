extern alias RelayApi;

using ApiProgram = RelayApi::Program;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Relay.IntegrationTests;

public sealed class ApiApplicationFactory : WebApplicationFactory<ApiProgram>
{
    private readonly string _connectionString;
    private readonly DateTimeOffset _utcNow;
    private readonly string? _previousConnectionString;
    private readonly string? _previousReceiverBaseUrl;
    private readonly string? _previousDataProtectionKeyPath;
    private bool _environmentRestored;

    public ApiApplicationFactory(string connectionString, DateTimeOffset utcNow)
    {
        _connectionString = connectionString;
        _utcNow = utcNow;
        DataProtectionKeyDirectory = Path.Combine(
            Path.GetTempPath(),
            "relay-integration-keys",
            Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(DataProtectionKeyDirectory);

        _previousConnectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Relay");
        _previousReceiverBaseUrl =
            Environment.GetEnvironmentVariable("Relay__ReceiverBaseUrl");
        _previousDataProtectionKeyPath =
            Environment.GetEnvironmentVariable("Relay__DataProtectionKeyPath");
        Environment.SetEnvironmentVariable("ConnectionStrings__Relay", _connectionString);
        Environment.SetEnvironmentVariable(
            "Relay__ReceiverBaseUrl",
            "http://receiver.test:8080");
        Environment.SetEnvironmentVariable(
            "Relay__DataProtectionKeyPath",
            DataProtectionKeyDirectory);
    }

    public string DataProtectionKeyDirectory { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Relay"] = _connectionString,
                ["Relay:ReceiverBaseUrl"] = "http://receiver.test:8080",
                ["Relay:DataProtectionKeyPath"] = DataProtectionKeyDirectory,
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(_utcNow));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing || _environmentRestored)
        {
            return;
        }

        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Relay",
            _previousConnectionString);
        Environment.SetEnvironmentVariable(
            "Relay__ReceiverBaseUrl",
            _previousReceiverBaseUrl);
        Environment.SetEnvironmentVariable(
            "Relay__DataProtectionKeyPath",
            _previousDataProtectionKeyPath);
        _environmentRestored = true;

        if (Directory.Exists(DataProtectionKeyDirectory))
        {
            Directory.Delete(DataProtectionKeyDirectory, recursive: true);
        }
    }
}
