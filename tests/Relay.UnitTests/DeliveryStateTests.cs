using Relay.Core;

namespace Relay.UnitTests;

public sealed class DeliveryStateTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DeliveryCanMoveFromQueuedToProcessingToSucceeded()
    {
        var delivery = CreateDelivery();
        var claimToken = Guid.NewGuid();
        var startedAtUtc = CreatedAtUtc.AddSeconds(1);
        var completedAtUtc = startedAtUtc.AddSeconds(2);

        delivery.Claim(claimToken, startedAtUtc);
        delivery.MarkSucceeded(claimToken, completedAtUtc);

        Assert.Equal(DeliveryState.Succeeded, delivery.State);
        Assert.Null(delivery.ClaimToken);
        Assert.Equal(startedAtUtc, delivery.StartedAtUtc);
        Assert.Equal(completedAtUtc, delivery.CompletedAtUtc);
        Assert.Null(delivery.ErrorCode);
        Assert.Null(delivery.ErrorMessage);
    }

    [Fact]
    public void DeliveryCanMoveFromQueuedToProcessingToFailed()
    {
        var delivery = CreateDelivery();
        var claimToken = Guid.NewGuid();
        var startedAtUtc = CreatedAtUtc.AddSeconds(1);
        var completedAtUtc = startedAtUtc.AddSeconds(2);

        delivery.Claim(claimToken, startedAtUtc);
        delivery.MarkFailed(claimToken, "http_status", "The receiver returned HTTP 500.", completedAtUtc);

        Assert.Equal(DeliveryState.Failed, delivery.State);
        Assert.Equal("http_status", delivery.ErrorCode);
        Assert.Equal("The receiver returned HTTP 500.", delivery.ErrorMessage);
        Assert.Equal(completedAtUtc, delivery.CompletedAtUtc);
    }

    [Fact]
    public void DeliveryRejectsInvalidClaimsAndDoubleCompletion()
    {
        var delivery = CreateDelivery();
        var claimToken = Guid.NewGuid();
        delivery.Claim(claimToken, CreatedAtUtc.AddSeconds(1));

        Assert.Throws<InvalidOperationException>(() =>
            delivery.Claim(Guid.NewGuid(), CreatedAtUtc.AddSeconds(2)));
        Assert.Throws<InvalidOperationException>(() =>
            delivery.MarkSucceeded(Guid.NewGuid(), CreatedAtUtc.AddSeconds(3)));

        delivery.MarkSucceeded(claimToken, CreatedAtUtc.AddSeconds(3));

        Assert.Throws<InvalidOperationException>(() =>
            delivery.MarkSucceeded(claimToken, CreatedAtUtc.AddSeconds(4)));
        Assert.Throws<InvalidOperationException>(() =>
            delivery.MarkFailed(
                claimToken,
                "http_status",
                "The receiver returned HTTP 500.",
                CreatedAtUtc.AddSeconds(4)));
    }

    [Fact]
    public void AttemptCanCompleteSuccessfully()
    {
        var startedAtUtc = CreatedAtUtc.AddSeconds(1);
        var completedAtUtc = startedAtUtc.AddMilliseconds(25);
        var attempt = new DeliveryAttempt(Guid.NewGuid(), Guid.NewGuid(), 1, startedAtUtc);

        attempt.MarkSucceeded(204, completedAtUtc, 25);

        Assert.Equal(AttemptState.Succeeded, attempt.State);
        Assert.Equal(204, attempt.HttpStatusCode);
        Assert.Equal(completedAtUtc, attempt.CompletedAtUtc);
        Assert.Equal(25, attempt.DurationMilliseconds);
        Assert.Null(attempt.ErrorCode);
        Assert.Null(attempt.ErrorMessage);
    }

    [Fact]
    public void AttemptCanFailAndRejectsDoubleCompletion()
    {
        var startedAtUtc = CreatedAtUtc.AddSeconds(1);
        var completedAtUtc = startedAtUtc.AddMilliseconds(25);
        var attempt = new DeliveryAttempt(Guid.NewGuid(), Guid.NewGuid(), 1, startedAtUtc);

        attempt.MarkFailed(500, "http_status", "The receiver returned HTTP 500.", completedAtUtc, 25);

        Assert.Equal(AttemptState.Failed, attempt.State);
        Assert.Equal(500, attempt.HttpStatusCode);
        Assert.Equal("http_status", attempt.ErrorCode);
        Assert.Equal("The receiver returned HTTP 500.", attempt.ErrorMessage);
        Assert.Throws<InvalidOperationException>(() =>
            attempt.MarkSucceeded(204, completedAtUtc.AddSeconds(1), 30));
        Assert.Throws<InvalidOperationException>(() =>
            attempt.MarkFailed(
                null,
                "transport_error",
                "The receiver could not be reached.",
                completedAtUtc.AddSeconds(1),
                30));
    }


    [Fact]
    public void ClaimSetsLeaseAndIncrementsAttemptCount()
    {
        var delivery = CreateDelivery();
        var claimToken = Guid.NewGuid();
        var startedAtUtc = CreatedAtUtc.AddSeconds(1);

        delivery.Claim(claimToken, startedAtUtc);

        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(startedAtUtc, delivery.ClaimedAtUtc);
        Assert.Equal(startedAtUtc.AddSeconds(30), delivery.ClaimExpiresAtUtc);
    }

    [Fact]
    public void ClaimAcceptsQueuedAndRetryScheduled()
    {
        var delivery = CreateDelivery();
        delivery.Claim(Guid.NewGuid(), CreatedAtUtc.AddSeconds(1));
        delivery.ScheduleRetry(CreatedAtUtc.AddSeconds(5));

        Assert.Equal(DeliveryState.RetryScheduled, delivery.State);

        var token2 = Guid.NewGuid();
        delivery.Claim(token2, CreatedAtUtc.AddSeconds(6));

        Assert.Equal(2, delivery.AttemptCount);
        Assert.Equal(DeliveryState.Processing, delivery.State);
    }

    [Fact]
    public void ScheduleRetryClearsClaimFields()
    {
        var delivery = CreateDelivery();
        delivery.Claim(Guid.NewGuid(), CreatedAtUtc.AddSeconds(1));
        
        var nextAttemptAt = CreatedAtUtc.AddSeconds(5);
        delivery.ScheduleRetry(nextAttemptAt);

        Assert.Equal(DeliveryState.RetryScheduled, delivery.State);
        Assert.Null(delivery.ClaimToken);
        Assert.Null(delivery.ClaimedAtUtc);
        Assert.Null(delivery.ClaimExpiresAtUtc);
        Assert.Equal(nextAttemptAt, delivery.NextAttemptAtUtc);
    }

    [Fact]
    public void ScheduleRetryRejectedFromNonProcessingStates()
    {
        var delivery = CreateDelivery();
        Assert.Throws<InvalidOperationException>(() => delivery.ScheduleRetry(CreatedAtUtc.AddSeconds(5)));
        
        var claimToken = Guid.NewGuid();
        delivery.Claim(claimToken, CreatedAtUtc.AddSeconds(1));
        delivery.MarkSucceeded(claimToken, CreatedAtUtc.AddSeconds(2));
        
        Assert.Throws<InvalidOperationException>(() => delivery.ScheduleRetry(CreatedAtUtc.AddSeconds(5)));
    }

    [Fact]
    public void RecoverStaleClaimSchedulesImmediateRetryAndClearsClaimFields()
    {
        var delivery = CreateDelivery();
        delivery.Claim(Guid.NewGuid(), CreatedAtUtc.AddSeconds(1));
        
        var recoveryTime = CreatedAtUtc.AddSeconds(40);
        delivery.RecoverStaleClaim(recoveryTime);

        Assert.Null(delivery.ClaimToken);
        Assert.Null(delivery.ClaimedAtUtc);
        Assert.Null(delivery.ClaimExpiresAtUtc);
        Assert.Equal(DeliveryState.RetryScheduled, delivery.State);
        Assert.Equal(recoveryTime, delivery.NextAttemptAtUtc);
    }

    [Fact]
    public void RecoverStaleClaimRejectedIfLeaseNotExpired()
    {
        var delivery = CreateDelivery();
        delivery.Claim(Guid.NewGuid(), CreatedAtUtc.AddSeconds(1));
        
        var recoveryTime = CreatedAtUtc.AddSeconds(20);
        Assert.Throws<InvalidOperationException>(() => delivery.RecoverStaleClaim(recoveryTime));
    }

    [Fact]
    public void MarkSucceededClearsErrorFields()
    {
        var delivery = CreateDelivery();
        var claimToken = Guid.NewGuid();
        delivery.Claim(claimToken, CreatedAtUtc.AddSeconds(1));
        delivery.MarkFailed(claimToken, "timeout", "error", CreatedAtUtc.AddSeconds(2));
        
        // Force state to Processing to simulate a weird transition or a replay/retry that kept the same delivery
        typeof(Delivery).GetProperty("State")!.SetValue(delivery, DeliveryState.RetryScheduled);
        
        var claimToken2 = Guid.NewGuid();
        delivery.Claim(claimToken2, CreatedAtUtc.AddSeconds(6));
        delivery.MarkSucceeded(claimToken2, CreatedAtUtc.AddSeconds(7));

        Assert.Null(delivery.ErrorCode);
        Assert.Null(delivery.ErrorMessage);
    }

    [Fact]
    public void RetryPolicyClassificationTests()
    {
        Assert.True(RetryPolicy.IsRetryable("timeout", null));
        Assert.True(RetryPolicy.IsRetryable("transport_error", null));
        Assert.True(RetryPolicy.IsRetryable("claim_expired", null));
        Assert.True(RetryPolicy.IsRetryable("http_status", 408));
        Assert.True(RetryPolicy.IsRetryable("http_status", 429));
        Assert.True(RetryPolicy.IsRetryable("http_status", 500));
        Assert.True(RetryPolicy.IsRetryable("http_status", 503));
        Assert.True(RetryPolicy.IsRetryable("http_status", 599));

        Assert.False(RetryPolicy.IsRetryable("target_not_allowed", null));
        Assert.False(RetryPolicy.IsRetryable("secret_unavailable", null));
        Assert.False(RetryPolicy.IsRetryable("http_status", 400));
        Assert.False(RetryPolicy.IsRetryable("http_status", 401));
        Assert.False(RetryPolicy.IsRetryable("http_status", 403));
        Assert.False(RetryPolicy.IsRetryable("http_status", 404));
    }

    private static Delivery CreateDelivery() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "{\"type\":\"demo.created\"}",
            new string('a', 64),
            Guid.NewGuid().ToString("N"),
            CreatedAtUtc);
}
