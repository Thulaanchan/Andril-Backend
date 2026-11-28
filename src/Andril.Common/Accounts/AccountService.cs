using Andril.Shared.Authentication;

namespace Andril.Common.Accounts;

public sealed class AccountService(ICurrentUser currentUser, IAccountRepository repository, TimeProvider clock)
    : IAccountService
{
    public async Task<AccountDto?> GetMeAsync(CancellationToken cancellationToken = default)
    {
        var subject = RequireSubject(cancellationToken);
        var account = await repository.FindBySubjectAsync(subject, cancellationToken);
        RequireUnchangedIdentity(subject, cancellationToken);
        return account is null ? null : ToDto(RequireOwnedAccount(account, subject));
    }

    public async Task<AccountDto> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var subject = RequireSubject(cancellationToken);
        var account = await repository.GetOrCreateAsync(subject, clock.GetUtcNow(), cancellationToken);
        RequireUnchangedIdentity(subject, cancellationToken);
        return ToDto(RequireOwnedAccount(account, subject));
    }

    private Guid RequireSubject(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!currentUser.IsAuthenticated || currentUser.UserId is not Guid subject || subject == Guid.Empty)
            throw new AccountAuthenticationRequiredException();
        return subject;
    }

    private void RequireUnchangedIdentity(Guid subject, CancellationToken cancellationToken)
    {
        if (RequireSubject(cancellationToken) != subject)
            throw new AccountAuthenticationRequiredException();
    }

    private static Account RequireOwnedAccount(Account? account, Guid subject) =>
        account is not null && account.Id != Guid.Empty && account.SupabaseUserId == subject
            ? account : throw new AccountStoreUnavailableException();

    private static AccountDto ToDto(Account account) => new(account.Id, account.CreatedAtUtc, account.UpdatedAtUtc);
}
