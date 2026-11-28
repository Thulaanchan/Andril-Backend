using Andril.Common.Accounts.Profiles;
using Andril.Learning.CV;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;

namespace Andril.Persistence.Repositories.Learning;

/// <summary>Private owner import over the same M2 serializable subject fence used by CV/Portfolio writers.</summary>
public static class LearningProjectImportPersistence
{
    public static ILearningProjectImportRepository Create(AccountsDbContext accounts,ICurrentUser user,
        IPrivateProfileService profiles,string role)=>new IsolatedLearningProjectImportRepository(
            new LearningOwnerDocumentTransaction(accounts,user,profiles,role,LearningPrivateDocument.ProjectImports));
}
