using System.Diagnostics;

namespace Relay.Api;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    private static readonly object ContextItemKey = new();

    public async Task InvokeAsync(HttpContext context, ILogger<CorrelationIdMiddleware> logger)
    {
        var correlationId = ResolveCorrelationId(context.Request.Headers);
        context.Items[ContextItemKey] = correlationId;
        Activity.Current?.SetTag("relay.correlation_id", correlationId);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
        }))
        {
            await next(context);
        }
    }

    public static string GetCorrelationId(HttpContext context) =>
        context.Items.TryGetValue(ContextItemKey, out var value) && value is string correlationId
            ? correlationId
            : throw new InvalidOperationException("Correlation middleware has not run.");

    private static string ResolveCorrelationId(IHeaderDictionary headers)
    {
        if (headers.TryGetValue(HeaderName, out var values)
            && values.Count is 1
            && IsValid(values[0]))
        {
            return values[0]!;
        }

        return Guid.NewGuid().ToString("N");
    }

    private static bool IsValid(string? value) =>
        value is { Length: > 0 and <= 64 }
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.');
}
