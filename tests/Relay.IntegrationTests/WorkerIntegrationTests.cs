extern alias RelayWorker;

using DeliveryProcessor = RelayWorker::Relay.Worker.DeliveryProcessor;
using WorkerServiceExtensions = RelayWorker::Relay.Worker.WorkerServiceExtensions;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Relay.Core;
using Relay.Infrastructure;

namespace Relay.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class WorkerIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset TestUtcNow =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlFixture _database;

    public WorkerIntegrationTests(PostgreSqlFixture database)
    {
        _database = database;
    }

    public Task InitializeAsync() => _database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ConcurrentProcessorsClaimOnceAndRecordOneSignedTerminalAttempt()
    {
        const string receiverOrigin = "http://receiver.test:8080";
        var signingSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var receiverId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");
        var targetUrl = $"{receiverOrigin}/webhooks/{receiverId:D}";
        var envelopeJson =
            $"{{\"eventId\":\"{eventId:D}\",\"deliveryId\":\"{deliveryId:D}\",\"type\":\"demo.created\",\"payload\":{{\"value\":1}}}}";
        var keyDirectory = Path.Combine(
            Path.GetTempPath(),
            "relay-worker-test-keys",
            Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(keyDirectory);

        try
        {
            var dataProtectionProvider = DataProtectionProvider.Create(
                new DirectoryInfo(keyDirectory));
            var secretProtector = new EndpointSecretProtector(dataProtectionProvider);
            await SeedQueuedDeliveryAsync(
                endpointId,
                eventId,
                deliveryId,
                targetUrl,
                secretProtector.Protect(signingSecret),
                envelopeJson,
                correlationId);

            var timeProvider = new FixedTimeProvider(TestUtcNow);
            var handler = new RecordingSuccessHandler();
            using var httpClientFactory = new DeterministicHttpClientFactory(handler);
            var targetPolicy = new DemoWebhookTargetPolicy(receiverOrigin);

            await using var firstDatabase = _database.CreateDbContext();
            await using var secondDatabase = _database.CreateDbContext();
            var firstProcessor = new DeliveryProcessor(
                firstDatabase,
                httpClientFactory,
                targetPolicy,
                secretProtector,
                timeProvider,
                NullLogger<DeliveryProcessor>.Instance);
            var secondProcessor = new DeliveryProcessor(
                secondDatabase,
                httpClientFactory,
                targetPolicy,
                secretProtector,
                timeProvider,
                NullLogger<DeliveryProcessor>.Instance);

            var processingResults = await Task.WhenAll(
                firstProcessor.TryProcessNextAsync(CancellationToken.None),
                secondProcessor.TryProcessNextAsync(CancellationToken.None));

            Assert.Equal(1, processingResults.Count(result => result));
            Assert.Equal(1, processingResults.Count(result => !result));
            var recordedRequest = Assert.Single(handler.Requests);
            Assert.Equal(targetUrl, recordedRequest.Url);
            Assert.Equal(eventId.ToString("D"), recordedRequest.EventId);
            Assert.Equal(deliveryId.ToString("D"), recordedRequest.DeliveryId);
            Assert.Equal(
                TestUtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                recordedRequest.Timestamp);
            Assert.Equal(correlationId, recordedRequest.CorrelationId);
            Assert.Equal(envelopeJson, Encoding.UTF8.GetString(recordedRequest.Body));
            Assert.Equal(
                RelayRequestSigner.Sign(
                    signingSecret,
                    TestUtcNow.ToUnixTimeSeconds(),
                    deliveryId,
                    recordedRequest.Body),
                recordedRequest.Signature);

            await using (var verificationDatabase = _database.CreateDbContext())
            {
                var delivery = await verificationDatabase.Deliveries.AsNoTracking().SingleAsync();
                var attempt = await verificationDatabase.DeliveryAttempts.AsNoTracking().SingleAsync();
                Assert.Equal(DeliveryState.Succeeded, delivery.State);
                Assert.Null(delivery.ClaimToken);
                Assert.Equal(TestUtcNow, delivery.StartedAtUtc);
                Assert.Equal(TestUtcNow, delivery.CompletedAtUtc);
                Assert.Equal(AttemptState.Succeeded, attempt.State);
                Assert.Equal(1, attempt.AttemptNumber);
                Assert.Equal(204, attempt.HttpStatusCode);
                Assert.Equal(TestUtcNow, attempt.StartedAtUtc);
                Assert.Equal(TestUtcNow, attempt.CompletedAtUtc);
            }

            await using var finalDatabase = _database.CreateDbContext();
            var finalProcessor = new DeliveryProcessor(
                finalDatabase,
                httpClientFactory,
                targetPolicy,
                secretProtector,
                timeProvider,
                NullLogger<DeliveryProcessor>.Instance);
            Assert.False(await finalProcessor.TryProcessNextAsync(CancellationToken.None));
            Assert.Single(handler.Requests);
        }
        finally
        {
            if (Directory.Exists(keyDirectory))
            {
                Directory.Delete(keyDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingDeliveryContinuesAfterEndpointIsDisabled(bool retryScheduled)
    {
        const string receiverOrigin = "http://receiver.test:8080";
        var signingSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var endpointId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var targetUrl = $"{receiverOrigin}/webhooks/{Guid.NewGuid():D}";
        var envelopeJson =
            $"{{\"eventId\":\"{eventId:D}\",\"deliveryId\":\"{deliveryId:D}\",\"type\":\"demo.disabled\",\"payload\":{{\"value\":1}}}}";
        var keyDirectory = Path.Combine(
            Path.GetTempPath(),
            "relay-worker-disabled-endpoint-keys",
            Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(keyDirectory);

        try
        {
            var dataProtectionProvider = DataProtectionProvider.Create(new DirectoryInfo(keyDirectory));
            var secretProtector = new EndpointSecretProtector(dataProtectionProvider);
            await SeedQueuedDeliveryAsync(
                endpointId,
                eventId,
                deliveryId,
                targetUrl,
                secretProtector.Protect(signingSecret),
                envelopeJson,
                Guid.NewGuid().ToString("N"));

            await using (var setup = _database.CreateDbContext())
            {
                var endpoint = await setup.WebhookEndpoints.SingleAsync();
                if (retryScheduled)
                {
                    var setupDelivery = await setup.Deliveries.SingleAsync();
                    var startedAtUtc = TestUtcNow.AddSeconds(-3);
                    var completedAtUtc = TestUtcNow.AddSeconds(-2);
                    setupDelivery.Claim(Guid.NewGuid(), startedAtUtc);
                    setupDelivery.ScheduleRetry(TestUtcNow.AddSeconds(-1));
                    var attempt = new DeliveryAttempt(
                        Guid.NewGuid(),
                        setupDelivery.Id,
                        setupDelivery.AttemptCount,
                        startedAtUtc);
                    attempt.MarkFailed(
                        503,
                        "http_status",
                        "The receiver returned HTTP 503.",
                        completedAtUtc,
                        1000);
                    setup.DeliveryAttempts.Add(attempt);
                }
                endpoint.Disable();
                await setup.SaveChangesAsync();
            }

            var handler = new RecordingSuccessHandler();
            using var httpClientFactory = new DeterministicHttpClientFactory(handler);
            await using var database = _database.CreateDbContext();
            var processor = new DeliveryProcessor(
                database,
                httpClientFactory,
                new DemoWebhookTargetPolicy(receiverOrigin),
                secretProtector,
                new FixedTimeProvider(TestUtcNow),
                NullLogger<DeliveryProcessor>.Instance);

            Assert.True(await processor.TryProcessNextAsync(CancellationToken.None));

            await using var verification = _database.CreateDbContext();
            var delivery = await verification.Deliveries.AsNoTracking().SingleAsync();
            Assert.Equal(DeliveryState.Succeeded, delivery.State);
            Assert.Equal(retryScheduled ? 2 : 1, delivery.AttemptCount);
            Assert.Single(handler.Requests);
        }
        finally
        {
            if (Directory.Exists(keyDirectory))
            {
                Directory.Delete(keyDirectory, recursive: true);
            }
        }
    }


    [Fact]
    public async Task RetrySuccess()
    {
        const string receiverOrigin = "http://receiver.test:8080";
        var signingSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var receiverId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");
        var targetUrl = $"{receiverOrigin}/webhooks/{receiverId:D}";
        var envelopeJson =
            $"{{\"eventId\":\"{eventId:D}\",\"deliveryId\":\"{deliveryId:D}\",\"type\":\"demo.retry\",\"payload\":{{\"value\":1}}}}";
        var keyDirectory = Path.Combine(
            Path.GetTempPath(),
            "relay-worker-retry-keys",
            Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(keyDirectory);

        try
        {
            var dataProtectionProvider = DataProtectionProvider.Create(
                new DirectoryInfo(keyDirectory));
            var secretProtector = new EndpointSecretProtector(dataProtectionProvider);
            await SeedQueuedDeliveryAsync(
                endpointId,
                eventId,
                deliveryId,
                targetUrl,
                secretProtector.Protect(signingSecret),
                envelopeJson,
                correlationId);

            var timeProvider = new FixedTimeProvider(TestUtcNow);
            var handler = new SequenceHandler(
                new(HttpStatusCode.ServiceUnavailable),
                new(HttpStatusCode.ServiceUnavailable),
                new(HttpStatusCode.NoContent));
            using var httpClientFactory = new DeterministicHttpClientFactory(handler);
            var targetPolicy = new DemoWebhookTargetPolicy(receiverOrigin);

            await using var database = _database.CreateDbContext();
            var processor = new DeliveryProcessor(
                database,
                httpClientFactory,
                targetPolicy,
                secretProtector,
                timeProvider,
                NullLogger<DeliveryProcessor>.Instance);

            Assert.True(await processor.TryProcessNextAsync(CancellationToken.None));
            await using (var check = _database.CreateDbContext())
            {
                var delivery = await check.Deliveries.AsNoTracking().SingleAsync();
                Assert.Equal(DeliveryState.RetryScheduled, delivery.State);
                Assert.Equal(1, delivery.AttemptCount);
            }

            timeProvider = new FixedTimeProvider(TestUtcNow.AddSeconds(2));
            await using var database2 = _database.CreateDbContext();
            processor = new DeliveryProcessor(
                database2,
                httpClientFactory,
                targetPolicy,
                secretProtector,
                timeProvider,
                NullLogger<DeliveryProcessor>.Instance);

            Assert.True(await processor.TryProcessNextAsync(CancellationToken.None));
            await using (var check = _database.CreateDbContext())
            {
                var delivery = await check.Deliveries.AsNoTracking().SingleAsync();
                Assert.Equal(DeliveryState.RetryScheduled, delivery.State);
                Assert.Equal(2, delivery.AttemptCount);
            }

            timeProvider = new FixedTimeProvider(TestUtcNow.AddSeconds(5));
            await using var database3 = _database.CreateDbContext();
            processor = new DeliveryProcessor(
                database3,
                httpClientFactory,
                targetPolicy,
                secretProtector,
                timeProvider,
                NullLogger<DeliveryProcessor>.Instance);

            Assert.True(await processor.TryProcessNextAsync(CancellationToken.None));
            await using (var check = _database.CreateDbContext())
            {
                var delivery = await check.Deliveries.AsNoTracking().SingleAsync();
                Assert.Equal(DeliveryState.Succeeded, delivery.State);
                Assert.Equal(3, delivery.AttemptCount);

                var attempts = await check.DeliveryAttempts.AsNoTracking()
                    .OrderBy(a => a.AttemptNumber).ToListAsync();
                Assert.Equal(3, attempts.Count);
                Assert.Equal(AttemptState.Failed, attempts[0].State);
                Assert.Equal(503, attempts[0].HttpStatusCode);
                Assert.Equal(AttemptState.Failed, attempts[1].State);
                Assert.Equal(503, attempts[1].HttpStatusCode);
                Assert.Equal(AttemptState.Succeeded, attempts[2].State);
                Assert.Equal(204, attempts[2].HttpStatusCode);
            }
        }
        finally
        {
            if (Directory.Exists(keyDirectory))
            {
                Directory.Delete(keyDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RetryExhaustion()
    {
        const string receiverOrigin = "http://receiver.test:8080";
        var signingSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var receiverId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");
        var targetUrl = $"{receiverOrigin}/webhooks/{receiverId:D}";
        var envelopeJson =
            $"{{\"eventId\":\"{eventId:D}\",\"deliveryId\":\"{deliveryId:D}\",\"type\":\"demo.exhaust\",\"payload\":{{\"value\":1}}}}";
        var keyDirectory = Path.Combine(
            Path.GetTempPath(),
            "relay-worker-exhaust-keys",
            Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(keyDirectory);

        try
        {
            var dataProtectionProvider = DataProtectionProvider.Create(
                new DirectoryInfo(keyDirectory));
            var secretProtector = new EndpointSecretProtector(dataProtectionProvider);
            await SeedQueuedDeliveryAsync(
                endpointId,
                eventId,
                deliveryId,
                targetUrl,
                secretProtector.Protect(signingSecret),
                envelopeJson,
                correlationId);

            var baseTime = TestUtcNow;
            var handler = new SequenceHandler(
                new(HttpStatusCode.ServiceUnavailable),
                new(HttpStatusCode.ServiceUnavailable),
                new(HttpStatusCode.ServiceUnavailable),
                new(HttpStatusCode.ServiceUnavailable));
            using var httpClientFactory = new DeterministicHttpClientFactory(handler);
            var targetPolicy = new DemoWebhookTargetPolicy(receiverOrigin);

            for (var attempt = 1; attempt <= 4; attempt++)
            {
                var delay = attempt > 1 ? RelayLimits.RetryDelays[attempt - 2] : TimeSpan.Zero;
                var now = baseTime.Add(delay).AddSeconds(1);
                var timeProvider = new FixedTimeProvider(now);
                await using var database = _database.CreateDbContext();
                var processor = new DeliveryProcessor(
                    database,
                    httpClientFactory,
                    targetPolicy,
                    secretProtector,
                    timeProvider,
                    NullLogger<DeliveryProcessor>.Instance);

                Assert.True(await processor.TryProcessNextAsync(CancellationToken.None));

                await using var check = _database.CreateDbContext();
                var delivery = await check.Deliveries.AsNoTracking().SingleAsync();
                Assert.Equal(attempt, delivery.AttemptCount);

                if (attempt < 4)
                {
                    Assert.Equal(DeliveryState.RetryScheduled, delivery.State);
                }
                else
                {
                    Assert.Equal(DeliveryState.Failed, delivery.State);
                    Assert.Equal("http_status", delivery.ErrorCode);
                }

                baseTime = now;
            }

            await using var finalDatabase = _database.CreateDbContext();
            var finalProcessor = new DeliveryProcessor(
                finalDatabase,
                httpClientFactory,
                targetPolicy,
                secretProtector,
                new FixedTimeProvider(baseTime.AddHours(1)),
                NullLogger<DeliveryProcessor>.Instance);
            Assert.False(await finalProcessor.TryProcessNextAsync(CancellationToken.None));

            await using var verifyDatabase = _database.CreateDbContext();
            var attempts = await verifyDatabase.DeliveryAttempts.AsNoTracking()
                .OrderBy(a => a.AttemptNumber).ToListAsync();
            Assert.Equal(4, attempts.Count);
            Assert.All(attempts, a => Assert.Equal(AttemptState.Failed, a.State));
        }
        finally
        {
            if (Directory.Exists(keyDirectory))
            {
                Directory.Delete(keyDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PermanentFailure()
    {
        const string receiverOrigin = "http://receiver.test:8080";
        var signingSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var receiverId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");
        var targetUrl = $"{receiverOrigin}/webhooks/{receiverId:D}";
        var envelopeJson =
            $"{{\"eventId\":\"{eventId:D}\",\"deliveryId\":\"{deliveryId:D}\",\"type\":\"demo.perm\",\"payload\":{{\"value\":1}}}}";
        var keyDirectory = Path.Combine(
            Path.GetTempPath(),
            "relay-worker-perm-keys",
            Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(keyDirectory);

        try
        {
            var dataProtectionProvider = DataProtectionProvider.Create(
                new DirectoryInfo(keyDirectory));
            var secretProtector = new EndpointSecretProtector(dataProtectionProvider);
            await SeedQueuedDeliveryAsync(
                endpointId,
                eventId,
                deliveryId,
                targetUrl,
                secretProtector.Protect(signingSecret),
                envelopeJson,
                correlationId);

            var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.BadRequest));
            using var httpClientFactory = new DeterministicHttpClientFactory(handler);
            var targetPolicy = new DemoWebhookTargetPolicy(receiverOrigin);
            var timeProvider = new FixedTimeProvider(TestUtcNow);

            await using var database = _database.CreateDbContext();
            var processor = new DeliveryProcessor(
                database,
                httpClientFactory,
                targetPolicy,
                secretProtector,
                timeProvider,
                NullLogger<DeliveryProcessor>.Instance);

            Assert.True(await processor.TryProcessNextAsync(CancellationToken.None));

            await using var check = _database.CreateDbContext();
            var delivery = await check.Deliveries.AsNoTracking().SingleAsync();
            Assert.Equal(DeliveryState.Failed, delivery.State);
            Assert.Equal(1, delivery.AttemptCount);
            Assert.Equal("http_status", delivery.ErrorCode);

            var attempt = await check.DeliveryAttempts.AsNoTracking().SingleAsync();
            Assert.Equal(AttemptState.Failed, attempt.State);
            Assert.Equal(400, attempt.HttpStatusCode);
        }
        finally
        {
            if (Directory.Exists(keyDirectory))
            {
                Directory.Delete(keyDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StaleClaimRecovery()
    {
        const string receiverOrigin = "http://receiver.test:8080";
        var signingSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var receiverId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");
        var targetUrl = $"{receiverOrigin}/webhooks/{receiverId:D}";
        var envelopeJson =
            $"{{\"eventId\":\"{eventId:D}\",\"deliveryId\":\"{deliveryId:D}\",\"type\":\"demo.stale\",\"payload\":{{\"value\":1}}}}";
        var keyDirectory = Path.Combine(
            Path.GetTempPath(),
            "relay-worker-stale-keys",
            Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(keyDirectory);

        try
        {
            var dataProtectionProvider = DataProtectionProvider.Create(
                new DirectoryInfo(keyDirectory));
            var secretProtector = new EndpointSecretProtector(dataProtectionProvider);
            await SeedQueuedDeliveryAsync(
                endpointId,
                eventId,
                deliveryId,
                targetUrl,
                secretProtector.Protect(signingSecret),
                envelopeJson,
                correlationId);

            var staleClaimToken = Guid.NewGuid();
            var staleStartedAt = TestUtcNow.AddMinutes(-1);
            var staleClaimExpires = TestUtcNow.AddSeconds(-10);
            await using (var setupDb = _database.CreateDbContext())
            {
                var claimedDelivery = await setupDb.Deliveries.SingleAsync();
                claimedDelivery.Claim(staleClaimToken, staleStartedAt);
                setupDb.DeliveryAttempts.Add(new DeliveryAttempt(
                    Guid.NewGuid(),
                    claimedDelivery.Id,
                    claimedDelivery.AttemptCount,
                    staleStartedAt));
                setupDb.Entry(claimedDelivery).Property("ClaimExpiresAtUtc").CurrentValue = staleClaimExpires;
                await setupDb.SaveChangesAsync();
            }

            var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.NoContent));
            using var httpClientFactory = new DeterministicHttpClientFactory(handler);
            var targetPolicy = new DemoWebhookTargetPolicy(receiverOrigin);

            var recoveryTime = new FixedTimeProvider(TestUtcNow);
            await using var recoveryDatabase = _database.CreateDbContext();
            var recoveryProcessor = new DeliveryProcessor(
                recoveryDatabase,
                httpClientFactory,
                targetPolicy,
                secretProtector,
                recoveryTime,
                NullLogger<DeliveryProcessor>.Instance);

            Assert.True(await recoveryProcessor.TryProcessNextAsync(CancellationToken.None));

            await using var verifyDb = _database.CreateDbContext();
            var delivery = await verifyDb.Deliveries.AsNoTracking().SingleAsync();
            Assert.Equal(DeliveryState.RetryScheduled, delivery.State);
            Assert.Equal(1, delivery.AttemptCount);

            var attempts = await verifyDb.DeliveryAttempts.AsNoTracking()
                .OrderBy(a => a.AttemptNumber).ToListAsync();
            Assert.Single(attempts);
            Assert.Equal(AttemptState.Failed, attempts[0].State);
            Assert.Equal("claim_expired", attempts[0].ErrorCode);
        }
        finally
        {
            if (Directory.Exists(keyDirectory))
            {
                Directory.Delete(keyDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CompletionAfterClaimRecoveryDoesNotOverwriteRecoveredState()
    {
        const string receiverOrigin = "http://receiver.test:8080";
        var signingSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var endpointId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");
        var targetUrl = $"{receiverOrigin}/webhooks/{Guid.NewGuid():D}";
        var envelopeJson =
            $"{{\"eventId\":\"{eventId:D}\",\"deliveryId\":\"{deliveryId:D}\",\"type\":\"demo.stale-completion\",\"payload\":{{\"value\":1}}}}";
        var keyDirectory = Path.Combine(
            Path.GetTempPath(),
            "relay-worker-stale-completion-keys",
            Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(keyDirectory);

        try
        {
            var dataProtectionProvider = DataProtectionProvider.Create(
                new DirectoryInfo(keyDirectory));
            var secretProtector = new EndpointSecretProtector(dataProtectionProvider);
            await SeedQueuedDeliveryAsync(
                endpointId,
                eventId,
                deliveryId,
                targetUrl,
                secretProtector.Protect(signingSecret),
                envelopeJson,
                correlationId);

            var targetPolicy = new DemoWebhookTargetPolicy(receiverOrigin);
            var blockingHandler = new BlockingSuccessHandler();
            using var blockingFactory = new DeterministicHttpClientFactory(blockingHandler);
            await using var originalDatabase = _database.CreateDbContext();
            var originalProcessor = new DeliveryProcessor(
                originalDatabase,
                blockingFactory,
                targetPolicy,
                secretProtector,
                new FixedTimeProvider(TestUtcNow),
                NullLogger<DeliveryProcessor>.Instance);

            var originalRun = originalProcessor.TryProcessNextAsync(CancellationToken.None);
            await blockingHandler.RequestStarted.WaitAsync(TimeSpan.FromSeconds(5));

            await using (var recoveryDatabase = _database.CreateDbContext())
            {
                var recoveryProcessor = new DeliveryProcessor(
                    recoveryDatabase,
                    blockingFactory,
                    targetPolicy,
                    secretProtector,
                    new FixedTimeProvider(TestUtcNow.AddSeconds(31)),
                    NullLogger<DeliveryProcessor>.Instance);
                Assert.True(await recoveryProcessor.TryProcessNextAsync(CancellationToken.None));
            }

            blockingHandler.Release();
            Assert.True(await originalRun);

            await using (var recoveredDatabase = _database.CreateDbContext())
            {
                var recoveredDelivery = await recoveredDatabase.Deliveries
                    .AsNoTracking()
                    .SingleAsync();
                var recoveredAttempt = await recoveredDatabase.DeliveryAttempts
                    .AsNoTracking()
                    .SingleAsync();
                Assert.Equal(DeliveryState.RetryScheduled, recoveredDelivery.State);
                Assert.Equal(AttemptState.Failed, recoveredAttempt.State);
                Assert.Equal("claim_expired", recoveredAttempt.ErrorCode);
            }

            using var successFactory = new DeterministicHttpClientFactory(
                new SequenceHandler(new HttpResponseMessage(HttpStatusCode.NoContent)));
            await using var retryDatabase = _database.CreateDbContext();
            var retryProcessor = new DeliveryProcessor(
                retryDatabase,
                successFactory,
                targetPolicy,
                secretProtector,
                new FixedTimeProvider(TestUtcNow.AddSeconds(32)),
                NullLogger<DeliveryProcessor>.Instance);
            Assert.True(await retryProcessor.TryProcessNextAsync(CancellationToken.None));

            await using var verificationDatabase = _database.CreateDbContext();
            var delivery = await verificationDatabase.Deliveries.AsNoTracking().SingleAsync();
            var attempts = await verificationDatabase.DeliveryAttempts
                .AsNoTracking()
                .OrderBy(attempt => attempt.AttemptNumber)
                .ToListAsync();
            Assert.Equal(DeliveryState.Succeeded, delivery.State);
            Assert.Equal(2, delivery.AttemptCount);
            Assert.Equal([AttemptState.Failed, AttemptState.Succeeded], attempts.Select(attempt => attempt.State));
        }
        finally
        {
            if (Directory.Exists(keyDirectory))
            {
                Directory.Delete(keyDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DueTimeEnforcement()
    {
        const string receiverOrigin = "http://receiver.test:8080";
        var signingSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var receiverId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("N");
        var targetUrl = $"{receiverOrigin}/webhooks/{receiverId:D}";
        var envelopeJson =
            $"{{\"eventId\":\"{eventId:D}\",\"deliveryId\":\"{deliveryId:D}\",\"type\":\"demo.due\",\"payload\":{{\"value\":1}}}}";
        var keyDirectory = Path.Combine(
            Path.GetTempPath(),
            "relay-worker-due-keys",
            Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(keyDirectory);

        try
        {
            var dataProtectionProvider = DataProtectionProvider.Create(
                new DirectoryInfo(keyDirectory));
            var secretProtector = new EndpointSecretProtector(dataProtectionProvider);
            await SeedQueuedDeliveryAsync(
                endpointId,
                eventId,
                deliveryId,
                targetUrl,
                secretProtector.Protect(signingSecret),
                envelopeJson,
                correlationId);

            var handler = new SequenceHandler(
                new(HttpStatusCode.ServiceUnavailable),
                new(HttpStatusCode.NoContent));
            using var httpClientFactory = new DeterministicHttpClientFactory(handler);
            var targetPolicy = new DemoWebhookTargetPolicy(receiverOrigin);

            var timeProvider = new FixedTimeProvider(TestUtcNow);
            await using var firstDatabase = _database.CreateDbContext();
            var firstProcessor = new DeliveryProcessor(
                firstDatabase,
                httpClientFactory,
                targetPolicy,
                secretProtector,
                timeProvider,
                NullLogger<DeliveryProcessor>.Instance);
            Assert.True(await firstProcessor.TryProcessNextAsync(CancellationToken.None));

            await using (var check = _database.CreateDbContext())
            {
                var scheduledDelivery = await check.Deliveries.AsNoTracking().SingleAsync();
                Assert.Equal(DeliveryState.RetryScheduled, scheduledDelivery.State);
                Assert.NotNull(scheduledDelivery.NextAttemptAtUtc);
            }

            var earlyTime = new FixedTimeProvider(TestUtcNow.AddMilliseconds(500));
            await using var earlyDatabase = _database.CreateDbContext();
            var earlyProcessor = new DeliveryProcessor(
                earlyDatabase,
                httpClientFactory,
                targetPolicy,
                secretProtector,
                earlyTime,
                NullLogger<DeliveryProcessor>.Instance);
            Assert.False(await earlyProcessor.TryProcessNextAsync(CancellationToken.None));

            var dueTime = new FixedTimeProvider(TestUtcNow.AddSeconds(2));
            await using var dueDatabase = _database.CreateDbContext();
            var dueProcessor = new DeliveryProcessor(
                dueDatabase,
                httpClientFactory,
                targetPolicy,
                secretProtector,
                dueTime,
                NullLogger<DeliveryProcessor>.Instance);
            Assert.True(await dueProcessor.TryProcessNextAsync(CancellationToken.None));

            await using var verifyDb = _database.CreateDbContext();
            var delivery = await verifyDb.Deliveries.AsNoTracking().SingleAsync();
            Assert.Equal(DeliveryState.Succeeded, delivery.State);
            Assert.Equal(2, delivery.AttemptCount);
        }
        finally
        {
            if (Directory.Exists(keyDirectory))
            {
                Directory.Delete(keyDirectory, recursive: true);
            }
        }
    }

    private async Task SeedQueuedDeliveryAsync(
        Guid endpointId,
        Guid eventId,
        Guid deliveryId,
        string targetUrl,
        string protectedSigningSecret,
        string envelopeJson,
        string correlationId)
    {
        var endpoint = new WebhookEndpoint(
            endpointId,
            $"Receiver {Guid.NewGuid():N}",
            targetUrl,
            protectedSigningSecret,
            TestUtcNow);
        var webhookEvent = new WebhookEvent(
            eventId,
            endpointId,
            "demo.created",
            "{\"value\":1}",
            $"event:{Guid.NewGuid():N}",
            Convert.ToHexStringLower(SHA256.HashData(RandomNumberGenerator.GetBytes(32))),
            correlationId,
            TestUtcNow);
        var delivery = new Delivery(
            deliveryId,
            eventId,
            endpointId,
            envelopeJson,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(envelopeJson))),
            correlationId,
            TestUtcNow);

        await using var database = _database.CreateDbContext();
        database.AddRange(endpoint, webhookEvent, delivery);
        await database.SaveChangesAsync();
    }

    private sealed class DeterministicHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client;

        public DeterministicHttpClientFactory(HttpMessageHandler handler)
        {
            _client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        public HttpClient CreateClient(string name)
        {
            if (!string.Equals(
                    name,
                    WorkerServiceExtensions.DeliveryClientName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The worker requested an unexpected HTTP client.");
            }

            return _client;
        }

        public void Dispose()
        {
            _client.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class RecordingSuccessHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<RecordedRequest> _requests = [];

        public IReadOnlyList<RecordedRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var recordedRequest = new RecordedRequest(
                request.RequestUri!.AbsoluteUri,
                request.Headers.GetValues("X-Relay-Event-Id").Single(),
                request.Headers.GetValues("X-Relay-Delivery-Id").Single(),
                request.Headers.GetValues("X-Relay-Timestamp").Single(),
                request.Headers.GetValues("X-Relay-Signature").Single(),
                request.Headers.GetValues("X-Correlation-Id").Single(),
                body);

            lock (_gate)
            {
                _requests.Add(recordedRequest);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed record RecordedRequest(
        string Url,
        string EventId,
        string DeliveryId,
        string Timestamp,
        string Signature,
        string CorrelationId,
        byte[] Body);

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = _index < responses.Length
                ? responses[_index++]
                : responses[^1];
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingSuccessHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _requestStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RequestStarted => _requestStarted.Task;

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requestStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
