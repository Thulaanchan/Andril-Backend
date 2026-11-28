using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Social;

public sealed class SocialLocationSettings
{
    public SocialLocationSettings(decimal cellSizeDegrees, TimeSpan maximumAge)
    {
        if (cellSizeDegrees < 0.01m || cellSizeDegrees > 90 || decimal.Round(cellSizeDegrees, 2) != cellSizeDegrees)
            throw new ArgumentOutOfRangeException(nameof(cellSizeDegrees));
        if (maximumAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumAge));
        CellSizeDegrees = cellSizeDegrees;
        MaximumAge = maximumAge;
    }

    public decimal CellSizeDegrees { get; }
    public TimeSpan MaximumAge { get; }
}

public sealed class PostgresSocialLocationRepository(IPrivateProfileService profiles,
    PostgresSocialQuestionnaireLifecycleRepository scope, SocialLocationSettings settings, TimeProvider clock)
{
    public Task UpdateSelfAsync(decimal latitude, decimal longitude, bool consent, CancellationToken token = default)
    {
        if (!consent) throw new ArgumentException("Explicit Social location consent is required.", nameof(consent));
        if (latitude is < -90 or > 90) throw new ArgumentOutOfRangeException(nameof(latitude));
        if (longitude is < -180 or > 180) throw new ArgumentOutOfRangeException(nameof(longitude));

        var latitudeCell = Coarsen(latitude, 90);
        var longitudeCell = Coarsen(longitude, 180);
        if (longitudeCell == 180) longitudeCell = -180;
        return CommitAsync(latitudeCell, longitudeCell, token);
    }

    public Task WithdrawSelfAsync(CancellationToken token = default) => CommitAsync(null, null, token);

    private decimal Coarsen(decimal coordinate, decimal maximum) => Math.Clamp(
        Math.Round(coordinate / settings.CellSizeDegrees, 0, MidpointRounding.AwayFromZero) * settings.CellSizeDegrees,
        -maximum, maximum);

    private Task CommitAsync(decimal? latitude, decimal? longitude, CancellationToken token) =>
        profiles.WithAuthorizedProfileAsync("social", profile => scope.InAuthorizedTransactionAsync(profile.ProfileId, async (db, ct) =>
        {
            await db.Database.ExecuteSqlRawAsync("SELECT andril_social.social_safety_lock()", ct);
            var now = clock.GetUtcNow().ToUniversalTime();
            return await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO andril_social.social_location_evidence AS stored
                    (profile_id, module, latitude_cell, longitude_cell, observed_at_utc, revision)
                    VALUES ({profile.ProfileId}, 'social', {latitude}, {longitude}, {now}, 1)
                ON CONFLICT (profile_id) DO UPDATE SET latitude_cell = EXCLUDED.latitude_cell,
                    longitude_cell = EXCLUDED.longitude_cell, observed_at_utc = EXCLUDED.observed_at_utc, revision = stored.revision + 1
                WHERE EXCLUDED.latitude_cell IS NOT NULL OR stored.latitude_cell IS NOT NULL
                """, ct);
        }, token), token);
}

public static class SocialApproximateDistance
{
    public static int? Kilometres(decimal? firstLatitude, decimal? firstLongitude, DateTimeOffset? firstObserved,
        decimal? secondLatitude, decimal? secondLongitude, DateTimeOffset? secondObserved, DateTimeOffset now, TimeSpan maximumAge)
    {
        if (firstLatitude is null || firstLongitude is null || secondLatitude is null || secondLongitude is null
            || firstObserved is null || secondObserved is null || firstObserved > now || secondObserved > now
            || now - firstObserved.Value > maximumAge || now - secondObserved.Value > maximumAge) return null;

        const double radians = Math.PI / 180;
        const double earthMeanRadiusKm = 6371.0088;
        double latitudeA = (double)firstLatitude.Value * radians;
        double latitudeB = (double)secondLatitude.Value * radians;
        double longitudeDifference = (double)(secondLongitude.Value - firstLongitude.Value) * radians;
        double latSin = Math.Sin((latitudeB - latitudeA) / 2);
        double lonSin = Math.Sin(longitudeDifference / 2);
        double haversine = Math.Clamp(latSin * latSin + Math.Cos(latitudeA) * Math.Cos(latitudeB) * lonSin * lonSin, 0, 1);
        return (int)Math.Round(2 * earthMeanRadiusKm * Math.Asin(Math.Sqrt(haversine)), MidpointRounding.AwayFromZero);
    }
}
