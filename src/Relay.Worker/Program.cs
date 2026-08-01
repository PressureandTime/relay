using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Relay.Infrastructure;
using Relay.Worker;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    options.UseUtcTimestamp = true;
});

builder.Services.AddRelayPersistence(builder.Configuration);
builder.Services.AddRelayDataProtection(builder.Configuration);
builder.Services.AddRelayTargetPolicy(builder.Configuration);
builder.Services.AddRelayDeliveryWorker();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<RelayDbContext>("postgres", tags: ["ready"]);

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
});

app.Run();

public partial class Program;
