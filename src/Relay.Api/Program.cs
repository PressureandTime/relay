using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Relay.Api;
using Relay.Infrastructure;
using System.Text.Json.Serialization;

var migrateOnly = args.Contains("--migrate", StringComparer.Ordinal);
var hostArguments = args.Where(argument => argument != "--migrate").ToArray();
var builder = WebApplication.CreateBuilder(hostArguments);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    options.UseUtcTimestamp = true;
});

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddRelayPersistence(builder.Configuration);
builder.Services.AddRelayDataProtection(builder.Configuration);
builder.Services.AddRelayTargetPolicy(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHealthChecks()
    .AddDbContextCheck<RelayDbContext>("postgres", tags: ["ready"]);

var app = builder.Build();

if (migrateOnly)
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
    await database.Database.MigrateAsync();
    return;
}

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.MapOpenApi("/api/openapi/{documentName}.json");
app.MapRelayApi();
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
