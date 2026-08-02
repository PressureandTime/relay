using System.Data;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Relay.Core;
using Relay.Infrastructure;

namespace Relay.Worker;

public sealed partial class DeliveryProcessor(
    RelayDbContext database,
    IHttpClientFactory httpClientFactory,
    DemoWebhookTargetPolicy targetPolicy,
    IEndpointSecretProtector secretProtector,
    TimeProvider timeProvider,
    ILogger<DeliveryProcessor> logger)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public async Task<bool> TryProcessNextAsync(CancellationToken stoppingToken)
    {
        if (await TryRecoverStaleClaimAsync(stoppingToken))
        {
            return true;
        }

        var workItem = await TryClaimAsync(stoppingToken);
        if (workItem is null)
        {
            return false;
        }

        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = workItem.CorrelationId,
            ["EventId"] = workItem.EventId,
            ["DeliveryId"] = workItem.DeliveryId,
            ["EndpointId"] = workItem.EndpointId,
        });
        LogClaimed(logger, workItem.DeliveryId, workItem.EventId, workItem.EndpointId);

        var startedTimestamp = Stopwatch.GetTimestamp();
        var outcome = await SendAsync(workItem, stoppingToken);
        var duration = Stopwatch.GetElapsedTime(startedTimestamp);
        var finalState = await CompleteAsync(workItem, outcome, duration, stoppingToken);
        LogCompleted(
            logger,
            workItem.DeliveryId,
            finalState,
            outcome.HttpStatusCode,
            (long)duration.TotalMilliseconds);
        return true;
    }

    private async Task<DeliveryWorkItem?> TryClaimAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var delivery = await database.Deliveries
            .FromSqlRaw(
                """
                SELECT *
                FROM deliveries
                WHERE "State" = 'Queued' OR ("State" = 'RetryScheduled' AND "NextAttemptAtUtc" <= {0})
                ORDER BY CASE WHEN "State" = 'Queued' THEN "CreatedAtUtc" ELSE "NextAttemptAtUtc" END
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """,
                now)
            .SingleOrDefaultAsync(cancellationToken);

        if (delivery is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var endpoint = await database.WebhookEndpoints
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == delivery.EndpointId, cancellationToken);
        var claimToken = Guid.CreateVersion7();
        delivery.Claim(claimToken, now);
        var attempt = new DeliveryAttempt(
            Guid.CreateVersion7(),
            delivery.Id,
            attemptNumber: delivery.AttemptCount,
            now);
        database.DeliveryAttempts.Add(attempt);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new DeliveryWorkItem(
            delivery.Id,
            delivery.EventId,
            delivery.EndpointId,
            attempt.Id,
            claimToken,
            endpoint.TargetUrl,
            endpoint.ProtectedSigningSecret,
            delivery.EnvelopeJson,
            delivery.CorrelationId);
    }

    private async Task<DeliveryOutcome> SendAsync(
        DeliveryWorkItem workItem,
        CancellationToken stoppingToken)
    {
        if (!targetPolicy.TryNormalize(workItem.TargetUrl, out var normalizedUrl, out _))
        {
            return DeliveryOutcome.Failure(null, "target_not_allowed", "The delivery target is not allowed.");
        }

        string signingSecret;
        try
        {
            signingSecret = secretProtector.Unprotect(workItem.ProtectedSigningSecret);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSecretUnavailable(logger, workItem.DeliveryId);
            return DeliveryOutcome.Failure(null, "secret_unavailable", "The signing secret could not be read.");
        }

        var body = Encoding.UTF8.GetBytes(workItem.EnvelopeJson);
        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, normalizedUrl);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        request.Headers.TryAddWithoutValidation("X-Relay-Event-Id", workItem.EventId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Relay-Delivery-Id", workItem.DeliveryId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Relay-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(
            "X-Relay-Signature",
            RelayRequestSigner.Sign(signingSecret, timestamp, workItem.DeliveryId, body));
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", workItem.CorrelationId);
        request.Headers.TryAddWithoutValidation("traceparent", CreateTraceParent());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            var client = httpClientFactory.CreateClient(WorkerServiceExtensions.DeliveryClientName);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var statusCode = (int)response.StatusCode;
            return response.IsSuccessStatusCode
                ? DeliveryOutcome.Success(statusCode)
                : DeliveryOutcome.Failure(
                    statusCode,
                    "http_status",
                    $"The receiver returned HTTP {statusCode}.");
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return DeliveryOutcome.Failure(null, "timeout", "The receiver did not respond before the timeout.");
        }
        catch (HttpRequestException)
        {
            return DeliveryOutcome.Failure(null, "transport_error", "The receiver could not be reached.");
        }
    }

    private async Task<DeliveryState> CompleteAsync(
        DeliveryWorkItem workItem,
        DeliveryOutcome outcome,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var delivery = await database.Deliveries
            .FromSqlRaw(
                """
                SELECT *
                FROM deliveries
                WHERE "Id" = {0}
                FOR UPDATE
                """,
                workItem.DeliveryId)
            .SingleAsync(cancellationToken);

        var attempt = await database.DeliveryAttempts
            .SingleAsync(candidate => candidate.Id == workItem.AttemptId, cancellationToken);

        if (delivery.State != DeliveryState.Processing || delivery.ClaimToken != workItem.ClaimToken)
        {
            LogStaleCompletion(
                logger,
                workItem.DeliveryId,
                delivery.State,
                workItem.ClaimToken,
                delivery.ClaimToken);
            await transaction.CommitAsync(cancellationToken);
            return delivery.State;
        }

        var completedAtUtc = timeProvider.GetUtcNow();
        var durationMilliseconds = Math.Max(0, (long)duration.TotalMilliseconds);

        if (outcome.Succeeded)
        {
            delivery.MarkSucceeded(workItem.ClaimToken, completedAtUtc);
            attempt.MarkSucceeded(outcome.HttpStatusCode!.Value, completedAtUtc, durationMilliseconds);
        }
        else
        {
            var isRetryable = RetryPolicy.IsRetryable(outcome.ErrorCode!, outcome.HttpStatusCode);
            var attemptsRemaining = delivery.AttemptCount < RelayLimits.MaxDeliveryAttempts;

            if (isRetryable && attemptsRemaining)
            {
                var delay = RelayLimits.RetryDelays[delivery.AttemptCount - 1];
                delivery.ScheduleRetry(completedAtUtc.Add(delay));
            }
            else
            {
                delivery.MarkFailed(
                    workItem.ClaimToken,
                    outcome.ErrorCode!,
                    outcome.ErrorMessage!,
                    completedAtUtc);
            }

            attempt.MarkFailed(
                outcome.HttpStatusCode,
                outcome.ErrorCode!,
                outcome.ErrorMessage!,
                completedAtUtc,
                durationMilliseconds);
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return delivery.State;
    }

    private async Task<bool> TryRecoverStaleClaimAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var delivery = await database.Deliveries
            .FromSqlRaw(
                """
                SELECT *
                FROM deliveries
                WHERE "State" = 'Processing' AND "ClaimExpiresAtUtc" < {0}
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """,
                now)
            .SingleOrDefaultAsync(cancellationToken);

        if (delivery is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        var attempt = await database.DeliveryAttempts
            .Where(a => a.DeliveryId == delivery.Id && a.State == AttemptState.Processing)
            .SingleOrDefaultAsync(cancellationToken);

        if (attempt is not null)
        {
            attempt.MarkFailed(
                null,
                "claim_expired",
                "The delivery claim expired before completion.",
                now,
                (long)(now - attempt.StartedAtUtc).TotalMilliseconds);
        }

        var claimToken = delivery.ClaimToken ?? Guid.Empty;

        if (delivery.AttemptCount < RelayLimits.MaxDeliveryAttempts)
        {
            delivery.RecoverStaleClaim(now);
        }
        else
        {
            delivery.MarkFailed(
                claimToken,
                "claim_expired",
                "The delivery claim expired before completion.",
                now);
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static string CreateTraceParent() =>
        $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01";

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Information,
        Message = "Claimed delivery {DeliveryId} for event {EventId} and endpoint {EndpointId}")]
    private static partial void LogClaimed(
        ILogger logger,
        Guid deliveryId,
        Guid eventId,
        Guid endpointId);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Information,
        Message = "Completed delivery {DeliveryId} as {DeliveryState} with HTTP {HttpStatusCode} in {DurationMs} ms")]
    private static partial void LogCompleted(
        ILogger logger,
        Guid deliveryId,
        DeliveryState deliveryState,
        int? httpStatusCode,
        long durationMs);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Warning,
        Message = "Signing secret was unavailable for delivery {DeliveryId}")]
    private static partial void LogSecretUnavailable(ILogger logger, Guid deliveryId);

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Warning,
        Message = "Delivery {DeliveryId} was modified by another process before completion. State: {DeliveryState}, Expected token: {ExpectedToken}, Actual token: {ActualToken}")]
    private static partial void LogStaleCompletion(
        ILogger logger,
        Guid deliveryId,
        DeliveryState deliveryState,
        Guid? expectedToken,
        Guid? actualToken);

    private sealed record DeliveryWorkItem(
        Guid DeliveryId,
        Guid EventId,
        Guid EndpointId,
        Guid AttemptId,
        Guid ClaimToken,
        string TargetUrl,
        string ProtectedSigningSecret,
        string EnvelopeJson,
        string CorrelationId);

    private sealed record DeliveryOutcome(
        bool Succeeded,
        int? HttpStatusCode,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public static DeliveryOutcome Success(int httpStatusCode) =>
            new(true, httpStatusCode, null, null);

        public static DeliveryOutcome Failure(
            int? httpStatusCode,
            string errorCode,
            string errorMessage) =>
            new(false, httpStatusCode, errorCode, errorMessage);
    }
}
