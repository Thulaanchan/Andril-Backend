using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Andril.Persistence.Repositories.Social;

public sealed record SocialInterestCatalogueItem(Guid Id, string DisplayName);
public sealed record SocialInterestSelection(Guid[]? InterestIds, long Revision);

public sealed class PostgresSocialInterestRepository(IPrivateProfileService profiles, PostgresSocialQuestionnaireLifecycleRepository scope)
{
    public Task<List<SocialInterestCatalogueItem>> ReadCatalogueAsync(CancellationToken token = default) =>
        OwnAsync((db, actor, ct) => db.Database.SqlQuery<SocialInterestCatalogueItem>($"""
            SELECT interest_id AS "Id", display_name AS "DisplayName"
            FROM andril_social.read_social_interest_catalogue({actor})
            """).ToListAsync(ct), token);

    public Task<SocialInterestSelection> ReadSelfAsync(CancellationToken token = default) =>
        OwnAsync((db, actor, ct) => db.Database.SqlQuery<SocialInterestSelection>($"""
            SELECT interest_ids AS "InterestIds", revision AS "Revision"
            FROM andril_social.read_own_social_interests({actor})
            """).SingleAsync(ct), token);

    public Task ReplaceAsync(Guid[]? ids, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var snapshot = ids.ToArray();
        if (snapshot.Contains(Guid.Empty) || snapshot.Distinct().Count() != snapshot.Length)
            throw new ArgumentException("Invalid Social interest selection.", nameof(ids));
        return OwnAsync((db, actor, ct) => db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT andril_social.replace_own_social_interests({actor}, {snapshot})
            """, ct), token);
    }

    private Task<T> OwnAsync<T>(Func<SocialDbContext, Guid, CancellationToken, Task<T>> action, CancellationToken token) =>
        profiles.WithAuthorizedProfileAsync("social", async profile =>
        {
            try
            {
                return await scope.InAuthorizedTransactionAsync(profile.ProfileId, (db, ct) => action(db, profile.ProfileId, ct), token);
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.InvalidParameterValue)
            {
                throw new ArgumentException("Invalid Social interest selection.");
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                throw new ProfileAccessDeniedException();
            }
        }, token);
}
