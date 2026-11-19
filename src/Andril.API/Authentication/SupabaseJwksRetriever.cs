using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Andril.API.Authentication;

/// <summary>Supabase publishes JWKS directly, not an OIDC discovery document here.</summary>
public sealed class SupabaseJwksRetriever(string issuer, string algorithm)
    : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        if (address != issuer + "/.well-known/jwks.json")
            throw new InvalidOperationException("Unexpected authentication key endpoint.");

        var document = await retriever.GetDocumentAsync(address, cancel);
        var jwks = new JsonWebKeySet(document);
        var configuration = new OpenIdConnectConfiguration { Issuer = issuer };
        foreach (var key in jwks.Keys)
        {
            if (string.IsNullOrWhiteSpace(key.Kid) ||
                (!string.IsNullOrEmpty(key.Use) && key.Use != "sig") ||
                (!string.IsNullOrEmpty(key.Alg) && key.Alg != algorithm) ||
                (key.KeyOps.Count > 0 && !key.KeyOps.Contains("verify")) ||
                (algorithm == "ES256" ? key.Kty != "EC" || key.Crv != "P-256" : key.Kty != "RSA"))
                continue;
            configuration.SigningKeys.Add(key);
        }
        if (configuration.SigningKeys.Count == 0)
            throw new InvalidOperationException("No compatible public signing keys are available.");
        return configuration;
    }
}
