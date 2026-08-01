using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Relay.ReceiverSimulator;

public sealed class ReceiverRegistry
{
    private const int SigningSecretByteCount = 32;
    private readonly ConcurrentDictionary<Guid, SyntheticReceiver> _receivers = new();
    private readonly string _publicBaseUrl;
    private readonly TimeProvider _timeProvider;

    public ReceiverRegistry(
        IOptions<ReceiverSimulatorOptions> options,
        TimeProvider timeProvider)
    {
        if (!ReceiverSimulatorOptions.TryNormalizePublicBaseUrl(
                options.Value.PublicBaseUrl,
                out _publicBaseUrl))
        {
            throw new InvalidOperationException("The configured receiver public base URL is invalid.");
        }

        _timeProvider = timeProvider;
    }

    public CreateReceiverResponse Create(string behavior)
    {
        var id = Guid.CreateVersion7();
        var signingSecret = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(SigningSecretByteCount));
        var receiver = new SyntheticReceiver(
            behavior,
            Encoding.UTF8.GetBytes(signingSecret));

        if (!_receivers.TryAdd(id, receiver))
        {
            throw new InvalidOperationException("The generated receiver identifier already exists.");
        }

        return new CreateReceiverResponse(
            id,
            $"{_publicBaseUrl}/webhooks/{id:D}",
            signingSecret);
    }

    public bool TryGet(Guid id, [NotNullWhen(true)] out SyntheticReceiver? receiver) =>
        _receivers.TryGetValue(id, out receiver);

    public ReceiverApplicationResult Record(
        SyntheticReceiver receiver,
        Guid eventId,
        Guid deliveryId,
        long timestamp,
        string correlationId,
        ReadOnlySpan<byte> bodyHash) =>
        receiver.Record(
            eventId,
            deliveryId,
            timestamp,
            correlationId,
            _timeProvider.GetUtcNow(),
            bodyHash);
}

public sealed class SyntheticReceiver
{
    private readonly object _gate = new();
    private readonly List<StoredReceipt> _orderedReceipts = [];
    private readonly Dictionary<Guid, StoredReceipt> _receiptsByDeliveryId = [];
    private readonly byte[] _signingSecret;

    internal SyntheticReceiver(string behavior, byte[] signingSecret)
    {
        Behavior = behavior;
        _signingSecret = signingSecret;
    }

    public string Behavior { get; }

    private int GetStatusCodeForAttempt(int receiveCount)
    {
        if (Behavior == "success") return 204;
        if (Behavior == "alwaysFail") return 500;
        if (Behavior == "retryThenSucceed") return receiveCount >= 3 ? 204 : 503;
        if (Behavior == "failUntilReplay") return receiveCount >= 5 ? 204 : 503;
        return 500; // fallback if unknown behavior
    }

    public bool HasValidSignature(
        string timestamp,
        string deliveryId,
        ReadOnlySpan<byte> rawBody,
        string signature)
    {
        if (!TryDecodeSignature(signature, out var suppliedSignature))
        {
            return false;
        }

        var prefix = Encoding.UTF8.GetBytes($"v1\n{timestamp}\n{deliveryId}\n");
        var canonicalBytes = new byte[prefix.Length + rawBody.Length];
        prefix.CopyTo(canonicalBytes, 0);
        rawBody.CopyTo(canonicalBytes.AsSpan(prefix.Length));

        try
        {
            var expectedSignature = HMACSHA256.HashData(_signingSecret, canonicalBytes);
            try
            {
                return CryptographicOperations.FixedTimeEquals(
                    expectedSignature,
                    suppliedSignature);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedSignature);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalBytes);
            CryptographicOperations.ZeroMemory(suppliedSignature);
        }
    }

    public IReadOnlyList<ReceiverReceiptResponse> GetReceipts()
    {
        lock (_gate)
        {
            return _orderedReceipts
                .Select(receipt => receipt.ToResponse())
                .ToArray();
        }
    }

    internal ReceiverApplicationResult Record(
        Guid eventId,
        Guid deliveryId,
        long timestamp,
        string correlationId,
        DateTimeOffset receivedAtUtc,
        ReadOnlySpan<byte> bodyHash)
    {
        lock (_gate)
        {
            if (_receiptsByDeliveryId.TryGetValue(deliveryId, out var existingReceipt))
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        existingReceipt.BodyHash,
                        bodyHash))
                {
                    return ReceiverApplicationResult.Conflict;
                }

                existingReceipt.IncrementReceiveCount();
                var statusCode = GetStatusCodeForAttempt(existingReceipt.ReceiveCount);
                if (existingReceipt.StatusCode != 204)
                {
                    existingReceipt.StatusCode = statusCode;
                }
                else
                {
                    statusCode = 204;
                }
                
                return new ReceiverApplicationResult(
                    IsConflict: false,
                    IsDuplicate: true,
                    StatusCode: statusCode);
            }

            var initialStatusCode = GetStatusCodeForAttempt(1);
            var receipt = new StoredReceipt(
                eventId,
                deliveryId,
                timestamp,
                correlationId,
                initialStatusCode,
                receivedAtUtc,
                bodyHash.ToArray());
            _receiptsByDeliveryId.Add(deliveryId, receipt);
            _orderedReceipts.Add(receipt);
            return new ReceiverApplicationResult(
                IsConflict: false,
                IsDuplicate: false,
                StatusCode: initialStatusCode);
        }
    }

    private static bool TryDecodeSignature(string signature, out byte[] decodedSignature)
    {
        decodedSignature = [];
        const string versionPrefix = "v1=";

        if (!signature.StartsWith(versionPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var encodedSignature = signature[versionPrefix.Length..];
            decodedSignature = Convert.FromBase64String(encodedSignature);
            if (decodedSignature.Length == SHA256.HashSizeInBytes
                && string.Equals(
                    Convert.ToBase64String(decodedSignature),
                    encodedSignature,
                    StringComparison.Ordinal))
            {
                return true;
            }

            CryptographicOperations.ZeroMemory(decodedSignature);
            decodedSignature = [];
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed class StoredReceipt(
        Guid eventId,
        Guid deliveryId,
        long timestamp,
        string correlationId,
        int statusCode,
        DateTimeOffset receivedAtUtc,
        byte[] bodyHash)
    {
        public byte[] BodyHash { get; } = bodyHash;

        public int StatusCode { get; internal set; } = statusCode;

        public int ReceiveCount { get; private set; } = 1;

        public void IncrementReceiveCount() => ReceiveCount++;

        public ReceiverReceiptResponse ToResponse() =>
            new(
                eventId,
                deliveryId,
                timestamp,
                correlationId,
                StatusCode,
                ReceiveCount,
                receivedAtUtc);
    }
}

public readonly record struct ReceiverApplicationResult(
    bool IsConflict,
    bool IsDuplicate,
    int StatusCode)
{
    public static ReceiverApplicationResult Conflict { get; } =
        new(
            IsConflict: true,
            IsDuplicate: false,
            StatusCode: StatusCodes.Status409Conflict);
}
