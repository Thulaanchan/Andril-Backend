namespace Andril.Common.Accounts;

public interface IAccountService
{
    Task<AccountDto?> GetMeAsync(CancellationToken cancellationToken = default);
    Task<AccountDto> InitializeAsync(CancellationToken cancellationToken = default);
}
