using System.Text.Json.Serialization;

namespace Andril.Learning.Interfaces;

public sealed record CurrentLearningOwner([property:JsonIgnore] Guid AccountId,[property:JsonIgnore] Guid ProfileId)
{
    public override string ToString()=>"Current Learning owner [private references omitted]";
}
/// <summary>
/// Shared current-owner read/release boundary. Resolves trusted subject/internal account and
/// Common Learning membership/profile lifecycle. Never grants activity, AI processing,
/// discovery, blocking, provider dispatch, XP or cleanup-purpose permissions. No transferable
/// positive lease/cache; repositories still require their coordinated transaction boundary.
/// </summary>
public interface ICurrentLearningOwnerBoundary
{
    Task<T?> WithCurrentOwnerAsync<T>(Func<CurrentLearningOwner,CancellationToken,Task<T?>> read,
        CancellationToken cancellationToken=default,Guid? expectedAccountId=null) where T:class;
}
