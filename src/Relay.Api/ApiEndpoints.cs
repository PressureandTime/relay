using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Relay.Core;
using Relay.Infrastructure;

namespace Relay.Api;

public static class ApiEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string IdempotencyReplayedHeader = "Idempotency-Replayed";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapRelayApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").WithTags("Relay");

        api.MapPost("/endpoints", CreateEndpointAsync)
            .WithName("CreateEndpoint")
            .Produces<EndpointResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();
        api.MapGet("/endpoints", GetEndpointsAsync)
            .WithName("GetEndpoints")
            .Produces<IReadOnlyList<EndpointResponse>>();
        api.MapPost("/events", CreateEventAsync)
            .WithName("CreateEvent")
            .Produces<EventAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        api.MapGet("/deliveries/{deliveryId:guid}", GetDeliveryAsync)
            .WithName("GetDelivery")
            .Produces<DeliveryDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        api.MapGet("/deliveries", GetDeliveriesAsync)
            .WithName("GetDeliveries")
            .Produces<IReadOnlyList<DeliverySummaryResponse>>();
        api.MapPost("/deliveries/{deliveryId:guid}/replays", ReplayDeliveryAsync)
            .WithName("ReplayDelivery")
            .Produces<ReplayAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> CreateEndpointAsync(
        CreateEndpointRequest request,
        RelayDbContext database,
        DemoWebhookTargetPolicy targetPolicy,
        IEndpointSecretProtector secretProtector,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var errors = ValidateEndpoint(request, targetPolicy, out var name, out var normalizedUrl);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var endpoint = new WebhookEndpoint(
            Guid.CreateVersion7(),
            name,
            normalizedUrl,
            secretProtector.Protect(request.SigningSecret!),
            timeProvider.GetUtcNow());
        database.WebhookEndpoints.Add(endpoint);
        await database.SaveChangesAsync(cancellationToken);

        var response = ToEndpointResponse(endpoint);
        return Results.Created($"/api/endpoints/{endpoint.Id}", response);
    }

    private static async Task<IResult> GetEndpointsAsync(
        RelayDbContext database,
        CancellationToken cancellationToken)
    {
        var endpoints = await database.WebhookEndpoints
            .AsNoTracking()
            .OrderByDescending(endpoint => endpoint.CreatedAtUtc)
            .Select(endpoint => new EndpointResponse(
                endpoint.Id,
                endpoint.Name,
                endpoint.TargetUrl,
                endpoint.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return Results.Ok(endpoints);
    }

    private static async Task<IResult> CreateEventAsync(
        CreateEventRequest request,
        HttpContext context,
        RelayDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var errors = ValidateEvent(request, context.Request.Headers, out var eventType, out var idempotencyKey);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var payloadJson = JsonSerializer.Serialize(request.Payload, SerializerOptions);
        if (Encoding.UTF8.GetByteCount(payloadJson) > RelayLimits.MaximumPayloadBytes)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["payload"] = [$"Payloads may not exceed {RelayLimits.MaximumPayloadBytes} bytes."],
            });
        }

        var fingerprint = ComputeSha256($"{eventType}\n{payloadJson}");
        var existing = await FindExistingEventAsync(
            database,
            request.EndpointId,
            idempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            return BuildIdempotencyResult(context, existing, fingerprint);
        }

        var endpointExists = await database.WebhookEndpoints
            .AsNoTracking()
            .AnyAsync(endpoint => endpoint.Id == request.EndpointId, cancellationToken);
        if (!endpointExists)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Webhook endpoint not found.",
                extensions: ProblemExtensions(context));
        }

        var createdAtUtc = timeProvider.GetUtcNow();
        var eventId = Guid.CreateVersion7();
        var deliveryId = Guid.CreateVersion7();
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(context);
        var envelopeJson = JsonSerializer.Serialize(
            new DeliveryEnvelope(
                eventId,
                deliveryId,
                eventType,
                createdAtUtc,
                request.Payload),
            SerializerOptions);
        var webhookEvent = new WebhookEvent(
            eventId,
            request.EndpointId,
            eventType,
            payloadJson,
            idempotencyKey,
            fingerprint,
            correlationId,
            createdAtUtc);
        var delivery = new Delivery(
            deliveryId,
            eventId,
            request.EndpointId,
            envelopeJson,
            ComputeSha256(envelopeJson),
            correlationId,
            createdAtUtc);

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        database.WebhookEvents.Add(webhookEvent);
        database.Deliveries.Add(delivery);

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            database.ChangeTracker.Clear();
            var winner = await FindExistingEventAsync(
                database,
                request.EndpointId,
                idempotencyKey,
                cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return BuildIdempotencyResult(context, winner, fingerprint);
        }

        return Results.Accepted(
            $"/api/deliveries/{delivery.Id}",
            new EventAcceptedResponse(
                webhookEvent.Id,
                delivery.Id,
                delivery.State,
                correlationId));
    }

    private static async Task<IResult> GetDeliveryAsync(
        Guid deliveryId,
        RelayDbContext database,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var delivery = await (
            from candidate in database.Deliveries.AsNoTracking()
            join endpoint in database.WebhookEndpoints.AsNoTracking()
                on candidate.EndpointId equals endpoint.Id
            join webhookEvent in database.WebhookEvents.AsNoTracking()
                on candidate.EventId equals webhookEvent.Id
            where candidate.Id == deliveryId
            select new DeliveryProjection(
                candidate.Id,
                candidate.EventId,
                candidate.EndpointId,
                endpoint.Name,
                webhookEvent.EventType,
                candidate.State,
                candidate.CorrelationId,
                candidate.ErrorCode,
                candidate.ErrorMessage,
                candidate.CreatedAtUtc,
                candidate.StartedAtUtc,
                candidate.CompletedAtUtc,
                candidate.AttemptCount,
                RelayLimits.MaxDeliveryAttempts,
                candidate.NextAttemptAtUtc,
                candidate.ReplayOfDeliveryId))
            .SingleOrDefaultAsync(cancellationToken);

        if (delivery is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Delivery not found.",
                extensions: ProblemExtensions(context));
        }

        var attempts = await database.DeliveryAttempts
            .AsNoTracking()
            .Where(attempt => attempt.DeliveryId == deliveryId)
            .OrderBy(attempt => attempt.AttemptNumber)
            .Select(attempt => new DeliveryAttemptResponse(
                attempt.Id,
                attempt.AttemptNumber,
                attempt.State,
                attempt.HttpStatusCode,
                attempt.ErrorCode,
                attempt.ErrorMessage,
                attempt.StartedAtUtc,
                attempt.CompletedAtUtc,
                attempt.DurationMilliseconds))
            .ToListAsync(cancellationToken);

        return Results.Ok(ToDeliveryDetail(delivery, attempts));
    }

    private static async Task<IResult> GetDeliveriesAsync(
        int? limit,
        RelayDbContext database,
        CancellationToken cancellationToken)
    {
        var boundedLimit = Math.Clamp(limit ?? 20, 1, 100);
        var deliveries = await (
            from delivery in database.Deliveries.AsNoTracking()
            join endpoint in database.WebhookEndpoints.AsNoTracking()
                on delivery.EndpointId equals endpoint.Id
            join webhookEvent in database.WebhookEvents.AsNoTracking()
                on delivery.EventId equals webhookEvent.Id
            orderby delivery.CreatedAtUtc descending
            select new DeliverySummaryResponse(
                delivery.Id,
                delivery.EventId,
                delivery.EndpointId,
                endpoint.Name,
                webhookEvent.EventType,
                delivery.State,
                delivery.CorrelationId,
                delivery.ErrorCode,
                delivery.ErrorMessage,
                delivery.CreatedAtUtc,
                delivery.StartedAtUtc,
                delivery.CompletedAtUtc,
                delivery.AttemptCount,
                RelayLimits.MaxDeliveryAttempts,
                delivery.NextAttemptAtUtc))
            .Take(boundedLimit)
            .ToListAsync(cancellationToken);

        return Results.Ok(deliveries);
    }

    private static async Task<IResult> ReplayDeliveryAsync(
        Guid deliveryId,
        HttpContext context,
        RelayDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = GetIdempotencyKeyFromHeaders(context.Request.Headers);
        if (idempotencyKey is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["idempotencyKey"] = ["Exactly one Idempotency-Key header is required."],
            });
        }

        var originalDelivery = await database.Deliveries
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == deliveryId, cancellationToken);

        if (originalDelivery is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Delivery not found.",
                extensions: ProblemExtensions(context));
        }

        if (originalDelivery.State != DeliveryState.Failed)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Only failed deliveries can be replayed.",
                detail: $"Delivery {deliveryId} is in state {originalDelivery.State}.",
                extensions: ProblemExtensions(context));
        }

        var existingReplay = await FindReplayAsync(
            database,
            deliveryId,
            idempotencyKey,
            cancellationToken);

        if (existingReplay is not null)
        {
            return BuildReplayResult(context, deliveryId, existingReplay, replayed: true);
        }

        var originalEvent = await database.WebhookEvents
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == originalDelivery.EventId, cancellationToken);

        var createdAtUtc = timeProvider.GetUtcNow();
        var newEventId = originalEvent.Id;
        var newDeliveryId = Guid.CreateVersion7();
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(context);
        using var payloadDocument = JsonDocument.Parse(originalEvent.PayloadJson);
        var envelopeJson = JsonSerializer.Serialize(
            new DeliveryEnvelope(
                newEventId,
                newDeliveryId,
                originalEvent.EventType,
                createdAtUtc,
                payloadDocument.RootElement),
            SerializerOptions);

        var replayDelivery = new Delivery(
            newDeliveryId,
            newEventId,
            originalDelivery.EndpointId,
            envelopeJson,
            ComputeSha256(envelopeJson),
            correlationId,
            deliveryId,
            idempotencyKey,
            createdAtUtc);

        database.Deliveries.Add(replayDelivery);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            var winner = await FindReplayAsync(
                database,
                deliveryId,
                idempotencyKey,
                cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return BuildReplayResult(context, deliveryId, winner, replayed: true);
        }

        return BuildReplayResult(context, deliveryId, replayDelivery, replayed: false);
    }

    private static async Task<Delivery?> FindReplayAsync(
        RelayDbContext database,
        Guid originalDeliveryId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        await database.Deliveries
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.ReplayOfDeliveryId == originalDeliveryId
                    && candidate.ReplayIdempotencyKey == idempotencyKey,
                cancellationToken);

    private static IResult BuildReplayResult(
        HttpContext context,
        Guid originalDeliveryId,
        Delivery replay,
        bool replayed)
    {
        if (replayed)
        {
            context.Response.Headers[IdempotencyReplayedHeader] = "true";
        }

        return Results.Accepted(
            $"/api/deliveries/{replay.Id}",
            new ReplayAcceptedResponse(
                originalDeliveryId,
                replay.Id,
                replay.State,
                replay.CorrelationId));
    }

    private static string? GetIdempotencyKeyFromHeaders(IHeaderDictionary headers)
    {
        if (!headers.TryGetValue(IdempotencyKeyHeader, out var values) || values.Count is not 1)
        {
            return null;
        }

        var key = values[0]?.Trim() ?? string.Empty;
        if (key.Length is 0 or > RelayLimits.IdempotencyKeyLength
            || !key.All(character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':'))
        {
            return null;
        }

        return key;
    }

    private static Dictionary<string, string[]> ValidateEndpoint(
        CreateEndpointRequest request,
        DemoWebhookTargetPolicy targetPolicy,
        out string name,
        out string normalizedUrl)
    {
        var errors = new Dictionary<string, string[]>();
        name = request.Name?.Trim() ?? string.Empty;
        normalizedUrl = string.Empty;

        if (name.Length is 0 or > RelayLimits.EndpointNameLength)
        {
            errors["name"] = [$"Name must contain 1 to {RelayLimits.EndpointNameLength} characters."];
        }

        if (!targetPolicy.TryNormalize(request.Url, out normalizedUrl, out var urlError))
        {
            errors["url"] = [urlError];
        }

        if (request.SigningSecret is null
            || request.SigningSecret.Length is < RelayLimits.SigningSecretMinimumLength
                or > RelayLimits.SigningSecretLength)
        {
            errors["signingSecret"] =
                [$"Signing secret must contain {RelayLimits.SigningSecretMinimumLength} to {RelayLimits.SigningSecretLength} characters."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateEvent(
        CreateEventRequest request,
        IHeaderDictionary headers,
        out string eventType,
        out string idempotencyKey)
    {
        var errors = new Dictionary<string, string[]>();
        eventType = request.Type?.Trim() ?? string.Empty;
        idempotencyKey = string.Empty;

        if (request.EndpointId == Guid.Empty)
        {
            errors["endpointId"] = ["A webhook endpoint is required."];
        }

        if (eventType.Length is 0 or > RelayLimits.EventTypeLength
            || !eventType.All(character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.'))
        {
            errors["type"] =
                [$"Type must contain 1 to {RelayLimits.EventTypeLength} letters, numbers, dots, dashes, or underscores."];
        }

        if (!headers.TryGetValue(IdempotencyKeyHeader, out var values)
            || values.Count is not 1)
        {
            errors["idempotencyKey"] = ["Exactly one Idempotency-Key header is required."];
        }
        else
        {
            idempotencyKey = values[0]?.Trim() ?? string.Empty;
            if (idempotencyKey.Length is 0 or > RelayLimits.IdempotencyKeyLength
                || !idempotencyKey.All(character =>
                    char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '_' or '.' or ':'))
            {
                errors["idempotencyKey"] =
                    [$"Idempotency-Key must contain 1 to {RelayLimits.IdempotencyKeyLength} safe ASCII characters."];
            }
        }

        if (request.Payload.ValueKind is not JsonValueKind.Object)
        {
            errors["payload"] = ["Payload must be a JSON object."];
        }

        return errors;
    }

    private static async Task<ExistingEvent?> FindExistingEventAsync(
        RelayDbContext database,
        Guid endpointId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        await (
            from webhookEvent in database.WebhookEvents.AsNoTracking()
            join delivery in database.Deliveries.AsNoTracking()
                on webhookEvent.Id equals delivery.EventId
            where webhookEvent.EndpointId == endpointId
                && webhookEvent.IdempotencyKey == idempotencyKey
            orderby delivery.CreatedAtUtc
            select new ExistingEvent(
                webhookEvent.Id,
                delivery.Id,
                delivery.State,
                webhookEvent.RequestFingerprint,
                webhookEvent.CorrelationId))
            .FirstOrDefaultAsync(cancellationToken);

    private static IResult BuildIdempotencyResult(
        HttpContext context,
        ExistingEvent existing,
        string requestFingerprint)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(existing.RequestFingerprint),
                Encoding.ASCII.GetBytes(requestFingerprint)))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Idempotency key conflicts with an existing event.",
                detail: "Use a new idempotency key when the event type or payload changes.",
                extensions: ProblemExtensions(context));
        }

        context.Response.Headers[IdempotencyReplayedHeader] = "true";
        return Results.Accepted(
            $"/api/deliveries/{existing.DeliveryId}",
            new EventAcceptedResponse(
                existing.EventId,
                existing.DeliveryId,
                existing.State,
                existing.CorrelationId));
    }

    private static EndpointResponse ToEndpointResponse(WebhookEndpoint endpoint) =>
        new(endpoint.Id, endpoint.Name, endpoint.TargetUrl, endpoint.CreatedAtUtc);

    private static DeliveryDetailResponse ToDeliveryDetail(
        DeliveryProjection delivery,
        IReadOnlyList<DeliveryAttemptResponse> attempts) =>
        new(
            delivery.Id,
            delivery.EventId,
            delivery.EndpointId,
            delivery.EndpointName,
            delivery.EventType,
            delivery.State,
            delivery.CorrelationId,
            delivery.ErrorCode,
            delivery.ErrorMessage,
            delivery.CreatedAtUtc,
            delivery.StartedAtUtc,
            delivery.CompletedAtUtc,
            delivery.AttemptCount,
            delivery.MaxAttempts,
            delivery.NextAttemptAtUtc,
            delivery.ReplayOfDeliveryId,
            attempts);

    private static Dictionary<string, object?> ProblemExtensions(HttpContext context) =>
        new()
        {
            ["correlationId"] = CorrelationIdMiddleware.GetCorrelationId(context),
        };

    private static string ComputeSha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record DeliveryEnvelope(
        Guid EventId,
        Guid DeliveryId,
        string Type,
        DateTimeOffset OccurredAtUtc,
        JsonElement Payload);

    private sealed record ExistingEvent(
        Guid EventId,
        Guid DeliveryId,
        DeliveryState State,
        string RequestFingerprint,
        string CorrelationId);

    private sealed record DeliveryProjection(
        Guid Id,
        Guid EventId,
        Guid EndpointId,
        string EndpointName,
        string EventType,
        DeliveryState State,
        string CorrelationId,
        string? ErrorCode,
        string? ErrorMessage,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        int AttemptCount,
        int MaxAttempts,
        DateTimeOffset? NextAttemptAtUtc,
        Guid? ReplayOfDeliveryId);
}
