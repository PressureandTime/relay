using System.Text.Json;
using Relay.Core;

namespace Relay.Api;

public sealed record CreateEndpointRequest(
    string? Name,
    string? Url,
    string? SigningSecret);

public sealed record EndpointResponse(
    Guid Id,
    string Name,
    string Url,
    DateTimeOffset CreatedAtUtc);

public sealed record CreateEventRequest(
    Guid EndpointId,
    string? Type,
    JsonElement Payload);

public sealed record EventAcceptedResponse(
    Guid EventId,
    Guid DeliveryId,
    DeliveryState State,
    string CorrelationId);

public sealed record DeliverySummaryResponse(
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
    DateTimeOffset? NextAttemptAtUtc);

public sealed record DeliveryAttemptResponse(
    Guid Id,
    int AttemptNumber,
    AttemptState State,
    int? HttpStatusCode,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? DurationMilliseconds);

public sealed record DeliveryDetailResponse(
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
    Guid? ReplayOfDeliveryId,
    IReadOnlyList<DeliveryAttemptResponse> Attempts);
