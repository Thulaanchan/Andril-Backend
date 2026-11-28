using Andril.Common.Accounts.Profiles;
using Andril.Learning.Portfolio;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;

namespace Andril.Persistence.Repositories.Learning;

/// <summary>Private owner Portfolio commands within the real M2 serializable callback.
/// Grants no publication, AI disclosure, CV import, media fetching or retention permission.</summary>
public static class LearningPortfolioPersistence
{
    public static ILearningPortfolioRepository Create(AccountsDbContext accounts, ICurrentUser user,
        IPrivateProfileService profiles, string runtimeRole) => new IsolatedLearningPortfolioRepository(
            new LearningOwnerDocumentTransaction(accounts,user,profiles,runtimeRole,LearningPrivateDocument.Portfolio));
}
