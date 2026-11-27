using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Shared.Authentication;
using Xunit;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.UnitTests.Common;

public sealed class DisplayNameAvailabilityTests
{
    [Fact]
    public async Task PS_T10_MissingDisplayName_ReturnsUnavailable()
    {
        var (service, _) = CreateService();

        var res1 = await service.CheckAvailabilityAsync(ModuleNames.Learning, "");
        var res2 = await service.CheckAvailabilityAsync(ModuleNames.Learning, "   ");
        var res3 = await service.CheckAvailabilityAsync(ModuleNames.Learning, null);

        Assert.False(res1.IsAvailable);
        Assert.False(res2.IsAvailable);
        Assert.False(res3.IsAvailable);
    }

    [Fact]
    public async Task PS_T11_DuplicateSameModule_ReturnsUnavailable()
    {
        var (service, repo) = CreateService();
        repo.TakenInModule = true;

        var res = await service.CheckAvailabilityAsync(ModuleNames.Learning, "Alex");

        Assert.False(res.IsAvailable);
        Assert.Contains("already taken in this module", res.Reason);
    }

    [Fact]
    public async Task PS_T12_SameAccountCrossModuleReuse_ReturnsUnavailable()
    {
        var (service, repo) = CreateService();
        repo.TakenInModule = false;
        repo.UsedBySameAccount = true; // Same account uses "alex" in another module

        var res = await service.CheckAvailabilityAsync(ModuleNames.Hobbies, "Alex");

        Assert.False(res.IsAvailable);
        Assert.Contains("cannot reuse the same display name across your different module identities", res.Reason);
    }

    [Fact]
    public async Task PS_T13_DifferentAccountCrossModuleReuse_Allowed()
    {
        var (service, repo) = CreateService();
        repo.TakenInModule = false;
        repo.UsedBySameAccount = false;

        var res = await service.CheckAvailabilityAsync(ModuleNames.Hobbies, "Alex");

        Assert.True(res.IsAvailable);
        Assert.Equal("Alex", res.NormalizedName);
        Assert.Null(res.Reason);
    }

    private static (DisplayNameAvailabilityService Service, FakeAvailabilityRepo Repo) CreateService()
    {
        var userId = Guid.NewGuid();
        var account = Account.Create(userId, DateTimeOffset.UtcNow);
        var user = new FakeUser(userId, true);
        var accounts = new FakeAccountRepo(account);
        var repo = new FakeAvailabilityRepo();

        var service = new DisplayNameAvailabilityService(user, accounts, repo);
        return (service, repo);
    }

    private sealed record FakeUser(Guid? UserId, bool IsAuthenticated) : ICurrentUser;

    private sealed class FakeAccountRepo(Account account) : IAccountRepository
    {
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default) =>
            Task.FromResult<Account?>(subject == account.SupabaseUserId ? account : null);

        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(account);
    }

    private sealed class FakeAvailabilityRepo : IDisplayNameAvailabilityRepository
    {
        public bool TakenInModule { get; set; }
        public bool UsedBySameAccount { get; set; }

        public Task<bool> IsDisplayNameTakenInModuleAsync(string module, string canonicalName, Guid excludeAccountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(TakenInModule);

        public Task<bool> IsDisplayNameUsedByAccountAsync(Guid accountId, string canonicalName, string excludeModule, CancellationToken cancellationToken = default) =>
            Task.FromResult(UsedBySameAccount);
    }
}
