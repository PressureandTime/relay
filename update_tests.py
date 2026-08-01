import re

with open("tests/Relay.UnitTests/DeliveryStateTests.cs", "r") as f:
    content = f.read()

tests = """
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
    public void RecoverStaleClaimClearsClaimFields()
    {
        var delivery = CreateDelivery();
        delivery.Claim(Guid.NewGuid(), CreatedAtUtc.AddSeconds(1));
        
        var recoveryTime = CreatedAtUtc.AddSeconds(40);
        delivery.RecoverStaleClaim(recoveryTime);

        Assert.Null(delivery.ClaimToken);
        Assert.Null(delivery.ClaimedAtUtc);
        Assert.Null(delivery.ClaimExpiresAtUtc);
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
        delivery.ScheduleRetry(CreatedAtUtc.AddSeconds(5));
        
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

"""

content = re.sub(r'    private static Delivery CreateDelivery', tests + r'    private static Delivery CreateDelivery', content)

with open("tests/Relay.UnitTests/DeliveryStateTests.cs", "w") as f:
    f.write(content)

