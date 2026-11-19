using Andril.Shared.Authentication;

namespace Andril.Common.Accounts;

public sealed class AccountService(ICurrentUser currentUser, IAccountRepository repository, TimeProvider clock)
    : IAccountService
{
    public async Task<AccountDto?> GetMeAsync(CancellationToken cancellationToken = default)
    {
        var account = await repository.FindBySubjectAsync(RequireSubject(), cancellationToken);
        return account is null ? null : ToDto(account);
    }

    public async Task<AccountDto> InitializeAsync(CancellationToken cancellationToken = default) =>
        ToDto(await repository.GetOrCreateAsync(RequireSubject(), clock.GetUtcNow(), cancellationToken));

    private Guid RequireSubject()
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not Guid subject || subject == Guid.Empty)
            throw new AccountAuthenticationRequiredException();
        return subject;
    }

    private static AccountDto ToDto(Account account) => new(account.Id, account.CreatedAtUtc, account.UpdatedAtUtc);
}
