using Andril.Persistence.DbContexts;
using Owner = Andril.Persistence.Repositories.Learning.IsolatedLearningDocumentBoundary.Owner;

namespace Andril.Persistence.Repositories.Learning;

internal interface ILearningDocumentBoundary
{
    Task<T> Run<T>(Func<LearningDbContext, Owner, CancellationToken, Task<T>> action,
        Func<LearningDocumentFailure, T> failure, CancellationToken ct, bool write = false,
        Func<string, CancellationToken, Task>? checkpoint = null, Func<Exception, bool>? additionalConflict = null);
}
