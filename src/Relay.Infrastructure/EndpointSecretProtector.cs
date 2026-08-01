using Microsoft.AspNetCore.DataProtection;

namespace Relay.Infrastructure;

public interface IEndpointSecretProtector
{
    string Protect(string signingSecret);

    string Unprotect(string protectedSigningSecret);
}

public sealed class EndpointSecretProtector(IDataProtectionProvider provider)
    : IEndpointSecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector(
        "Relay.EndpointSigningSecret.v1");

    public string Protect(string signingSecret) => _protector.Protect(signingSecret);

    public string Unprotect(string protectedSigningSecret) =>
        _protector.Unprotect(protectedSigningSecret);
}
