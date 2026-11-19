using System.Globalization;
using System.Threading.RateLimiting;
using Andril.API.Authentication;
using Microsoft.AspNetCore.RateLimiting;

namespace Andril.API.Extensions;

public static class SupabaseIdentityServiceCollectionExtensions
{
    public static IServiceCollection AddSupabaseIdentityVerification(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient<ISupabaseUserVerifier, SupabaseUserVerifier>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
            client.MaxResponseContentBufferSize = 64 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
          .RemoveAllLoggers();

        // Technical local protection, not the Supabase OTP/password rate policy.
        services.AddRateLimiter(options =>
        {
            var limit = configuration.GetValue<int?>("SupabaseIdentity:AccountRequestsPerMinute") ?? 60;
            if (limit is < 1 or > 600) throw new InvalidOperationException("Account identity rate limit must be between 1 and 600.");
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!IdentityProtectedRoutes.Contains(context.Request.Path)
                    || VerifiedUserSubject.GetId(context.User) is not Guid subject)
                    return RateLimitPartition.GetNoLimiter("not-account-identity");
                return RateLimitPartition.GetFixedWindowLimiter(subject.ToString(), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                });
            });
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.StatusCode = 429;
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retry.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
        });
        return services;
    }
}
