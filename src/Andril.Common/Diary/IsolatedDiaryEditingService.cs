using Andril.Common.Accounts.Profiles;

namespace Andril.Common.Diary;

/// <summary>
/// CF-13 isolated, unregistered composition of private Diary reads and content operations.
/// Uses the existing module gate and server-resolved authorized profile callback; supplied
/// entry/profile references never grant access. Production PrivateModuleAccess remains pending
/// and fail-closed. No new M2 authority, Diary permission, persistence, endpoint or DI is supplied.
/// </summary>
/// <remarks>
/// This edits caller-held in-memory content only. Production requires reviewed Diary-purpose
/// authorization, lifecycle/revocation coordination and a transactional Diary store. Ordinary
/// inactive access must be denied by the module gate; this service never deletes data on lifecycle
/// changes. Reflection/plan removal uses existing local domain semantics, not entry deletion,
/// hard/soft deletion, retention, account deletion or durable rollback/concurrency guarantees.
/// </remarks>
public sealed class IsolatedDiaryEditingService
{
    private readonly IPrivateModuleAccess access;
    private readonly IPrivateProfileService profiles;

    public IsolatedDiaryEditingService(IPrivateModuleAccess access, IPrivateProfileService profiles)
    {
        ArgumentNullException.ThrowIfNull(access); ArgumentNullException.ThrowIfNull(profiles);
        this.access = access; this.profiles = profiles;
    }

    public Task EditReflectionAsync(ModuleDiaryEntry entry, string? title, string body, CancellationToken cancellationToken = default) =>
        EditAsync(entry, () => entry.EditReflection(title, body), cancellationToken);

    public Task RemoveReflectionAsync(ModuleDiaryEntry entry, CancellationToken cancellationToken = default) =>
        EditAsync(entry, entry is null ? null! : entry.RemoveReflection, cancellationToken);

    public Task EditPlanAsync(ModuleDiaryEntry entry, Guid planId, string title, string? description = null,
        CancellationToken cancellationToken = default) =>
        EditAsync(entry, () => entry.EditPlan(planId, title, description), cancellationToken);

    public Task RemovePlanAsync(ModuleDiaryEntry entry, Guid planId, CancellationToken cancellationToken = default) =>
        EditAsync(entry, () => entry.RemovePlan(planId), cancellationToken);

    /// <summary>Release a detached private snapshot only through the same owner/module checks.
    /// This is not a public DTO, export, AI context or authorization receipt. Entry loading and
    /// production read-release/revocation coordination remain separate pending integrations.</summary>
    public Task<DiaryEntrySnapshot> ReadSnapshotAsync(ModuleDiaryEntry entry, CancellationToken cancellationToken = default) =>
        WithOwnedEntryAsync(entry, () => new DiaryEntrySnapshot(entry), cancellationToken);

    private Task EditAsync(ModuleDiaryEntry entry, Action edit, CancellationToken ct) =>
        WithOwnedEntryAsync(entry, () => { edit(); return true; }, ct);

    private async Task<T> WithOwnedEntryAsync<T>(ModuleDiaryEntry entry, Func<T> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ct.ThrowIfCancellationRequested();
        await access.RequireAsync(entry.Module, ct);
        ct.ThrowIfCancellationRequested();
        return await profiles.WithAuthorizedProfileAsync(entry.Module, async profile =>
        {
            ct.ThrowIfCancellationRequested();
            if (profile.ProfileId == Guid.Empty || profile.ProfileId != entry.OwnerModuleProfileId || profile.Module != entry.Module)
                throw new ProfileAccessDeniedException();
            // Repeat ordinary module/lifecycle access at read release/mutation in the profile
            // callback. This is not a substitute for pending production revocation coordination.
            await access.RequireAsync(entry.Module, ct);
            ct.ThrowIfCancellationRequested();
            return operation(); // No asynchronous work or secondary dispatch after read/mutation.
        }, ct);
    }
}
