namespace Andril.Common.Accounts.Profiles;

public interface IProfileLocationRepository
{
    Task<ProfileLocationRecord?> GetByProfileIdAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<ProfileLocationRecord?> GetByAccountAndModuleAsync(Guid accountId, string module, CancellationToken cancellationToken = default);
    Task<ProfileLocationRecord> UpsertAsync(ProfileLocationRecord record, CancellationToken cancellationToken = default);
    Task<bool> DeleteByProfileIdAsync(Guid profileId, CancellationToken cancellationToken = default);
}
