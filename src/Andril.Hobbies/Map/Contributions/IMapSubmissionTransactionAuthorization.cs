using System.Data.Common;

namespace Andril.Hobbies.Map.Contributions;

/// <summary>Map-owned transaction-bound active Hobbies identity boundary; no production provider.</summary>
/// <remarks>Resolve the owner from trusted server identity and fresh module/profile evidence,
/// never a client or prepared-payload owner claim. Hold M2 authorization/revocation fences on
/// the supplied connection/transaction before Map resource locks, including on retries.
/// Return null for denial and throw for unavailability; never replace/commit this transaction.
/// Attraction activity is enforced separately under the lifecycle row lock.</remarks>
internal interface IMapSubmissionTransactionAuthorization
{
    Task<Guid?> ResolveActiveHobbiesOwnerAsync(DbConnection connection, DbTransaction transaction,
        CancellationToken cancellationToken = default);
}
