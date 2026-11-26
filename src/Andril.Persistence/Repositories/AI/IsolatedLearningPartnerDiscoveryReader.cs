using Andril.Common.Accounts.Profiles;
using Andril.Learning.Discovery;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Andril.Persistence.Repositories.AI;

/// <summary>New isolated evidence storage; not production Learning discoverability or opt-in policy.</summary>
internal sealed class IsolatedLearningPartnerDiscoveryRow
{
    public Guid AccountId { get; set; }
    public Guid ModuleProfileId { get; set; }
    public Guid CandidateProfileId { get; set; }
    public string Module { get; set; } = "learning";
    public LearningDiscoveryLifecycle Lifecycle { get; set; }
    public LearningDiscoveryDecision Eligibility { get; set; }
    public LearningDiscoveryDecision Visibility { get; set; }
    public LearningDiscoveryDecision Blocking { get; set; }
    public string? DisplayName { get; set; }
    public LearningDiscoveryDecision NameDisclosure { get; set; }
    public string? Interests { get; set; }
    public LearningDiscoveryDecision InterestsDisclosure { get; set; }
    public string? Skills { get; set; }
    public LearningDiscoveryDecision SkillsDisclosure { get; set; }
    public string? Goals { get; set; }
    public LearningDiscoveryDecision GoalsDisclosure { get; set; }
    public string? ProfessionalBiography { get; set; }
    public LearningDiscoveryDecision BiographyDisclosure { get; set; }
    public override string ToString() => "Learning partner source row [private content omitted]";
}

/// <summary>
/// Actual isolated PostgreSQL reader; runtime SELECT only. Production source/evidence writers
/// must coordinate lifecycle/privacy/block changes through the same owner boundary, atomically
/// binding exact field content to AI disclosure. No production writer or policy is inferred here.
/// </summary>
internal sealed class IsolatedLearningPartnerDiscoveryReader : ILearningPartnerDiscoveryReader
{
    private readonly IsolatedLearningDocumentBoundary boundary;
    internal Func<string, CancellationToken, Task>? TestCheckpoint { get; init; }
    internal IsolatedLearningPartnerDiscoveryReader(DbContextOptions<LearningDbContext> options,
        ICurrentUser user, IPrivateModuleAccess access, IPrivateProfileService profiles)
        => boundary = new(options, user, access, profiles);

    public Task<T?> WithAuthorizedSearchAsync<T>(Guid accountId, Guid learningProfileId, string query,
        Func<LearningPartnerDiscoverySnapshot, CancellationToken, Task<T>> project,
        CancellationToken cancellationToken = default) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(project);
        if (accountId == Guid.Empty || learningProfileId == Guid.Empty || string.IsNullOrWhiteSpace(query)) return Task.FromResult<T?>(null);
        return boundary.Run<T?>(async (parent, owner, ct) =>
        {
            var account = await parent.Database.SqlQueryRaw<string>("SELECT current_setting('andril.account',true) AS \"Value\"").SingleAsync(ct);
            if (owner.Profile != learningProfileId || account != accountId.ToString()) throw new ProfileAccessDeniedException();
            await using var db = new IsolatedLearningAiDbContext(new DbContextOptionsBuilder<IsolatedLearningAiDbContext>()
                .UseNpgsql((NpgsqlConnection)parent.Database.GetDbConnection()).Options);
            await db.Database.UseTransactionAsync(parent.Database.CurrentTransaction!.GetDbTransaction(), ct);
            var rows = await db.Set<IsolatedLearningPartnerDiscoveryRow>().AsNoTracking().Where(x =>
                x.AccountId == accountId && x.ModuleProfileId == learningProfileId && x.CandidateProfileId != learningProfileId &&
                x.Module == "learning" && x.Lifecycle == LearningDiscoveryLifecycle.Active &&
                x.Eligibility == LearningDiscoveryDecision.Allowed && x.Visibility == LearningDiscoveryDecision.Allowed && x.Blocking == LearningDiscoveryDecision.Allowed &&
                ((x.NameDisclosure == LearningDiscoveryDecision.Allowed && x.DisplayName != null && x.DisplayName.Contains(query)) ||
                 (x.InterestsDisclosure == LearningDiscoveryDecision.Allowed && x.Interests != null && x.Interests.Contains(query)) ||
                 (x.SkillsDisclosure == LearningDiscoveryDecision.Allowed && x.Skills != null && x.Skills.Contains(query)) ||
                 (x.GoalsDisclosure == LearningDiscoveryDecision.Allowed && x.Goals != null && x.Goals.Contains(query)) ||
                 (x.BiographyDisclosure == LearningDiscoveryDecision.Allowed && x.ProfessionalBiography != null && x.ProfessionalBiography.Contains(query))))
                .OrderBy(x => x.CandidateProfileId)
                .Select(x => new { x.CandidateProfileId,
                    DisplayName = x.NameDisclosure == LearningDiscoveryDecision.Allowed ? x.DisplayName : null,
                    Interests = x.InterestsDisclosure == LearningDiscoveryDecision.Allowed ? x.Interests : null,
                    Skills = x.SkillsDisclosure == LearningDiscoveryDecision.Allowed ? x.Skills : null,
                    Goals = x.GoalsDisclosure == LearningDiscoveryDecision.Allowed ? x.Goals : null,
                    Biography = x.BiographyDisclosure == LearningDiscoveryDecision.Allowed ? x.ProfessionalBiography : null })
                .ToListAsync(ct);
            static LearningPartnerField? Field(string? text) => text is null ? null : new(text, LearningDiscoveryDecision.Allowed);
            var evidence = rows.Select(x => new LearningPartnerEvidence(x.CandidateProfileId, "learning", LearningDiscoveryLifecycle.Active,
                LearningDiscoveryDecision.Allowed, LearningDiscoveryDecision.Allowed, LearningDiscoveryDecision.Allowed,
                Field(x.DisplayName), Field(x.Interests), Field(x.Skills), Field(x.Goals), Field(x.Biography))).ToArray();
            return await project(new(accountId, learningProfileId, Array.AsReadOnly(evidence)), ct);
        }, failure => failure == LearningDocumentFailure.Denied ? null : throw new InvalidOperationException("Learning discovery unavailable."),
            cancellationToken, checkpoint: TestCheckpoint);
    }
}
