extern alias RelayReceiver;

using ReceiverProgram = RelayReceiver::Program;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Relay.IntegrationTests;

public sealed class ReceiverApplicationFactory : WebApplicationFactory<ReceiverProgram>
{
    private readonly DateTimeOffset _utcNow;

    public ReceiverApplicationFactory(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:PublicBaseUrl"] = "http://receiver.test:8080",
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(_utcNow));
        });
    }
}
