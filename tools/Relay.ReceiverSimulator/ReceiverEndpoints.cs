using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace Relay.ReceiverSimulator;

public static partial class ReceiverEndpoints
{
    private const string EventIdHeader = "X-Relay-Event-Id";
    private const string DeliveryIdHeader = "X-Relay-Delivery-Id";
    private const string TimestampHeader = "X-Relay-Timestamp";
    private const string SignatureHeader = "X-Relay-Signature";
    private const string CorrelationIdHeader = "X-Correlation-Id";
    private const long AllowedTimestampSkewSeconds = 5 * 60;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static IEndpointRouteBuilder MapReceiverSimulator(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/_control/receivers", CreateReceiver)
            .WithName("CreateSyntheticReceiver")
            .Produces<CreateReceiverResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        endpoints.MapGet("/_control/receivers/{id:guid}/receipts", GetReceipts)
            .WithName("GetSyntheticReceiverReceipts")
            .Produces<IReadOnlyList<ReceiverReceiptResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        endpoints.MapPost("/webhooks/{id:guid}", ReceiveWebhookAsync)
            .WithName("ReceiveSyntheticWebhook")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    private static IResult CreateReceiver(
        CreateReceiverRequest request,
        ReceiverRegistry registry)
    {
        var behavior = request.Behavior?.Trim() ?? string.Empty;
        var isValid = behavior switch
        {
            "success" or "retryThenSucceed" or "failUntilReplay" or "alwaysFail" => true,
            _ => false,
        };

        if (!isValid)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["behavior"] = ["Behavior must be 'success', 'retryThenSucceed', 'failUntilReplay', or 'alwaysFail'."],
            });
        }

        var response = registry.Create(behavior);
        return Results.Created($"/_control/receivers/{response.Id:D}", response);
    }

    private static IResult GetReceipts(Guid id, ReceiverRegistry registry)
    {
        if (!registry.TryGet(id, out var receiver))
        {
            return Results.NotFound();
        }

        return Results.Ok(receiver.GetReceipts());
    }

    private static async Task<IResult> ReceiveWebhookAsync(
        Guid id,
        HttpRequest request,
        ReceiverRegistry registry,
        TimeProvider timeProvider,
        ILogger<ReceiverRegistry> logger,
        CancellationToken cancellationToken)
    {
        if (!registry.TryGet(id, out var receiver))
        {
            return Results.NotFound();
        }

        if (!TryReadCanonicalGuidHeader(request.Headers, EventIdHeader, out var eventId, out _)
            || !TryReadCanonicalGuidHeader(
                request.Headers,
                DeliveryIdHeader,
                out var deliveryId,
                out var deliveryIdText)
            || !TryReadCorrelationId(request.Headers, out var correlationId))
        {
            return Results.BadRequest();
        }

        if (!TryReadTimestamp(request.Headers, out var timestamp, out var timestampText)
            || IsOutsideAllowedTimestampWindow(timestamp, timeProvider.GetUtcNow())
            || !TryReadSingleHeader(request.Headers, SignatureHeader, out var signature))
        {
            return Results.Unauthorized();
        }

        byte[] rawBody;
        await using (var bodyBuffer = new MemoryStream())
        {
            await request.Body.CopyToAsync(bodyBuffer, cancellationToken);
            rawBody = bodyBuffer.ToArray();
        }

        try
        {
            if (!receiver.HasValidSignature(
                    timestampText,
                    deliveryIdText,
                    rawBody,
                    signature))
            {
                return Results.Unauthorized();
            }

            if (!IsValidUtf8(rawBody))
            {
                return Results.BadRequest();
            }

            var bodyHash = SHA256.HashData(rawBody);
            try
            {
                var applicationResult = registry.Record(
                    receiver,
                    eventId,
                    deliveryId,
                    timestamp,
                    correlationId,
                    bodyHash);

                if (applicationResult.IsConflict)
                {
                    LogDeliveryConflict(
                        logger,
                        eventId,
                        deliveryId,
                        correlationId);
                }
                else
                {
                    LogDeliveryReceived(
                        logger,
                        eventId,
                        deliveryId,
                        correlationId,
                        applicationResult.StatusCode,
                        applicationResult.IsDuplicate);
                }

                return Results.StatusCode(applicationResult.StatusCode);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bodyHash);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawBody);
        }
    }

    private static bool TryReadCanonicalGuidHeader(
        IHeaderDictionary headers,
        string name,
        out Guid value,
        out string text)
    {
        value = Guid.Empty;
        text = string.Empty;

        return TryReadSingleHeader(headers, name, out text)
            && Guid.TryParseExact(text, "D", out value)
            && value != Guid.Empty;
    }

    private static bool TryReadTimestamp(
        IHeaderDictionary headers,
        out long timestamp,
        out string text)
    {
        timestamp = default;
        text = string.Empty;

        return TryReadSingleHeader(headers, TimestampHeader, out text)
            && long.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out timestamp)
            && string.Equals(
                timestamp.ToString(CultureInfo.InvariantCulture),
                text,
                StringComparison.Ordinal);
    }

    private static bool TryReadCorrelationId(
        IHeaderDictionary headers,
        out string correlationId)
    {
        if (!TryReadSingleHeader(headers, CorrelationIdHeader, out correlationId))
        {
            return false;
        }

        return correlationId is { Length: > 0 and <= 64 }
            && correlationId.All(character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.');
    }

    private static bool TryReadSingleHeader(
        IHeaderDictionary headers,
        string name,
        out string value)
    {
        value = string.Empty;

        if (!headers.TryGetValue(name, out StringValues values)
            || values.Count != 1
            || string.IsNullOrEmpty(values[0]))
        {
            return false;
        }

        value = values[0]!;
        return true;
    }

    private static bool IsOutsideAllowedTimestampWindow(
        long timestamp,
        DateTimeOffset now)
    {
        var nowTimestamp = now.ToUnixTimeSeconds();
        return timestamp < nowTimestamp - AllowedTimestampSkewSeconds
            || timestamp > nowTimestamp + AllowedTimestampSkewSeconds;
    }

    private static bool IsValidUtf8(byte[] rawBody)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(rawBody);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Information,
        Message = "Received delivery {DeliveryId} for event {EventId} with correlation {CorrelationId}; returning HTTP {HttpStatusCode}; duplicate: {IsDuplicate}")]
    private static partial void LogDeliveryReceived(
        ILogger logger,
        Guid eventId,
        Guid deliveryId,
        string correlationId,
        int httpStatusCode,
        bool isDuplicate);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Warning,
        Message = "Rejected delivery {DeliveryId} for event {EventId} with correlation {CorrelationId} because the delivery identifier was reused with a different body")]
    private static partial void LogDeliveryConflict(
        ILogger logger,
        Guid eventId,
        Guid deliveryId,
        string correlationId);
}
