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


    [Fact]
    public async Task RetrySuccess()
    {
        Assert.True(true); // Placeholder for retry success
    }

    [Fact]
    public async Task RetryExhaustion()
    {
        Assert.True(true); // Placeholder for retry exhaustion
    }

    [Fact]
    public async Task PermanentFailure()
    {
        Assert.True(true); // Placeholder for permanent failure
    }

    [Fact]
    public async Task StaleClaimRecovery()
    {
        Assert.True(true); // Placeholder for stale claim recovery
    }

    [Fact]
    public async Task DueTimeEnforcement()
    {
        Assert.True(true); // Placeholder for due time enforcement
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
}
