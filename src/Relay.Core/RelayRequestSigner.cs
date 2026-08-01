using System.Security.Cryptography;
using System.Text;

namespace Relay.Core;

public static class RelayRequestSigner
{
    public const string SignaturePrefix = "v1=";

    public static string Sign(
        string signingSecret,
        long unixTimestamp,
        Guid deliveryId,
        ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(signingSecret);

        var key = Encoding.UTF8.GetBytes(signingSecret);
        var prefix = Encoding.UTF8.GetBytes(
            $"v1\n{unixTimestamp}\n{deliveryId:D}\n");
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(prefix);
        hmac.AppendData(body);
        return SignaturePrefix + Convert.ToBase64String(hmac.GetHashAndReset());
    }
}
