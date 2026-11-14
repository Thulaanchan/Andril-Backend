using Andril.API.Authentication;
using Andril.Shared.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Andril.API.Extensions;

public static class AuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddSupabaseAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            var settings = configuration.GetSection("SupabaseAuth").Get<SupabaseAuthOptions>() ?? new();
            options.MapInboundClaims = false;
            options.SaveToken = false;
            options.IncludeErrorDetails = false;
            options.RequireHttpsMetadata = true;
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    if (VerifiedUserSubject.GetId(context.Principal) is null)
                        context.Fail("A verified user identity is required.");
                    return Task.CompletedTask;
                }
            };

            if (!settings.TryGetIssuer(out var issuer))
            {
                // Keep public health available, but authenticate nobody until the
                // operator explicitly confirms a supported asymmetric signing key.
                options.Events.OnMessageReceived = context =>
                {
                    context.Fail("Authentication is not configured.");
                    return Task.CompletedTask;
                };
                return;
            }

            options.Backchannel = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(10),
                MaxResponseContentBufferSize = 1024 * 1024
            };
            options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                issuer + "/.well-known/jwks.json",
                new SupabaseJwksRetriever(issuer, settings.SigningAlgorithm),
                new HttpDocumentRetriever(options.Backchannel) { RequireHttps = true })
            {
                AutomaticRefreshInterval = TimeSpan.FromMinutes(10),
                RefreshInterval = TimeSpan.FromMinutes(1),
                UseLastKnownGoodConfiguration = false
            };
            options.RefreshOnIssuerKeyNotFound = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = settings.Audience,
                IgnoreTrailingSlashWhenValidatingAudience = false,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.Zero,
                ValidAlgorithms = [settings.SigningAlgorithm],
                NameClaimType = "sub",
                RoleClaimType = "role"
            };
        });
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().Build();
        });
        return services;
    }
}
