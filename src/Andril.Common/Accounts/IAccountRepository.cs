namespace Andril.Common.Accounts;

public interface IAccountRepository
{
    Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default);
    Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default);
}
