using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Relay.ReceiverSimulator;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    options.UseUtcTimestamp = true;
});
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services
    .AddOptions<ReceiverSimulatorOptions>()
    .BindConfiguration(ReceiverSimulatorOptions.SectionName)
    .Validate(
        ReceiverSimulatorOptions.HasValidPublicBaseUrl,
        $"{ReceiverSimulatorOptions.SectionName}:PublicBaseUrl must be an HTTP origin without a path, query, fragment, or user information.")
    .ValidateOnStart();
builder.Services.AddSingleton<ReceiverRegistry>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
});
app.MapHealthChecks("/health/ready");
app.MapReceiverSimulator();

app.Run();

public partial class Program;
