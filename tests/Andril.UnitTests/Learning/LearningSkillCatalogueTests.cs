using System.Text.Json;
using Andril.Learning.Entities;
using Xunit;
using Skill = Andril.Learning.Entities.LearningSkillCatalogue.Skill;

namespace Andril.UnitTests.Learning;

/// <summary>
/// Offline catalogue snapshot rules only. No authoritative reader, authorization,
/// persistence, administrative history or automatic preferences integration is proven here.
/// Display names and successor mappings are not modeled by the current API.
/// </summary>
public sealed class LearningSkillCatalogueTests
{
    private static Skill Active(Guid id) => new(id, true, LearningSkillCatalogueStatus.Active);
    private static LearningPreferences Preferences() => new(Guid.NewGuid(), Guid.NewGuid());

    [Theory]
    [InlineData(LearningSkillCatalogueStatus.Active)]
    [InlineData(LearningSkillCatalogueStatus.Retired)]
    [InlineData(LearningSkillCatalogueStatus.Superseded)]
    [InlineData(LearningSkillCatalogueStatus.Merged)]
    public void SkillFacts_RetainStableIdentityWithoutGrantingEligibility(LearningSkillCatalogueStatus status)
    {
        var id = Guid.NewGuid();
        var skill = new Skill(id, false, status);
        _ = new LearningSkillCatalogue([skill]);
        Assert.Equal(id, skill.Id);
        Assert.False(skill.IsApproved);
        Assert.Equal(status, skill.Status);
        Assert.All(typeof(Skill).GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void EmptyCatalogueIdentity_IsRejected() =>
        Assert.Throws<ArgumentException>("id", () => Active(Guid.Empty));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void UndefinedLifecycle_IsRejected(int status) =>
        Assert.Throws<ArgumentException>("status", () =>
            new Skill(Guid.NewGuid(), true, (LearningSkillCatalogueStatus)status));

    [Fact]
    public void NullCatalogue_IsRejected() =>
        Assert.Throws<ArgumentNullException>("skills", () => new LearningSkillCatalogue(null!));

    [Fact]
    public void NullCatalogueEntry_IsRejected() =>
        Assert.Throws<ArgumentNullException>(() => new LearningSkillCatalogue([null!]));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedCatalogueIdentity_IsRejectedInsteadOfChoosingFacts(bool conflictingFacts)
    {
        var id = Guid.NewGuid();
        var first = Active(id);
        var second = conflictingFacts ? new Skill(id, false, LearningSkillCatalogueStatus.Retired) : first;
        Assert.Throws<ArgumentException>("skills", () => new LearningSkillCatalogue([first, second]));
    }

    [Fact]
    public void ApprovedActiveSkills_SupportDistinctSelectionsAndTeachingLearningOverlap()
    {
        var shared = Guid.NewGuid();
        var teachingOnly = Guid.NewGuid();
        var learningOnly = Guid.NewGuid();
        var catalogue = new LearningSkillCatalogue([Active(shared), Active(teachingOnly), Active(learningOnly)]);
        catalogue.ValidateNewSelections([shared, teachingOnly], [shared, learningOnly]);
        catalogue.ValidateNewSelections([shared, teachingOnly], [shared, learningOnly]);
        // Revalidation neither consumes identities nor introduces cross-list duplicate rejection.
    }

    [Fact]
    public void EmptyProposedLists_DoNotInventMinimumSelectionRequirements() =>
        new LearningSkillCatalogue([]).ValidateNewSelections([], []);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownIdentity_IsRejectedWithoutDisclosingIt(bool teaching)
    {
        var unknown = Guid.NewGuid();
        var catalogue = new LearningSkillCatalogue([]);
        var exception = Assert.Throws<ArgumentException>(teaching ? "teachingSkills" : "learningSkills", () =>
            catalogue.ValidateNewSelections(teaching ? [unknown] : [], teaching ? [] : [unknown]));
        Assert.True(!exception.ToString().Contains(unknown.ToString(), StringComparison.Ordinal),
            "Catalogue failure diagnostics must omit supplied identities.");
    }

    [Theory]
    [InlineData(LearningSkillCatalogueStatus.Active)]
    [InlineData(LearningSkillCatalogueStatus.Retired)]
    [InlineData(LearningSkillCatalogueStatus.Superseded)]
    [InlineData(LearningSkillCatalogueStatus.Merged)]
    public void UnapprovedSkills_AreRejectedInBothLists(LearningSkillCatalogueStatus status)
    {
        var id = Guid.NewGuid();
        var catalogue = new LearningSkillCatalogue([new Skill(id, false, status)]);
        Assert.Throws<ArgumentException>("teachingSkills", () => catalogue.ValidateNewSelections([id], []));
        Assert.Throws<ArgumentException>("learningSkills", () => catalogue.ValidateNewSelections([], [id]));
    }

    [Theory]
    [InlineData(LearningSkillCatalogueStatus.Retired)]
    [InlineData(LearningSkillCatalogueStatus.Superseded)]
    [InlineData(LearningSkillCatalogueStatus.Merged)]
    public void ApprovedInactiveSkills_AreRejectedInBothLists(LearningSkillCatalogueStatus status)
    {
        var id = Guid.NewGuid();
        var catalogue = new LearningSkillCatalogue([new Skill(id, true, status)]);
        Assert.Throws<ArgumentException>("teachingSkills", () => catalogue.ValidateNewSelections([id], []));
        Assert.Throws<ArgumentException>("learningSkills", () => catalogue.ValidateNewSelections([], [id]));
    }

    [Theory]
    [InlineData(true, "empty")]
    [InlineData(false, "empty")]
    [InlineData(true, "duplicate")]
    [InlineData(false, "duplicate")]
    [InlineData(true, "unknown")]
    [InlineData(false, "unknown")]
    public void RejectedCandidate_PreservesOverlappingPreferencesAndReusableCatalogue(bool teaching, string defect)
    {
        var id = Guid.NewGuid();
        var catalogue = new LearningSkillCatalogue([Active(id)]);
        var preferences = Preferences();
        preferences.UpdateSelections([Guid.NewGuid()], [id], [id]);
        var goal = preferences.CreateGoal("Private test goal");
        var interests = preferences.Interests;
        var priorTeaching = preferences.TeachingSkills;
        var priorLearning = preferences.LearningSkills;
        var goals = preferences.Goals;
        Guid[] invalid = defect switch
        {
            "empty" => [Guid.Empty], "duplicate" => [id, id], _ => [Guid.NewGuid()]
        };
        Guid[] nextTeaching = teaching ? invalid : [id];
        Guid[] nextLearning = teaching ? [id] : invalid;
        // Explicit test composition only: production UpdateSelections does not consult a catalogue.
        Assert.Throws<ArgumentException>(teaching ? "teachingSkills" : "learningSkills", () =>
        {
            catalogue.ValidateNewSelections(nextTeaching, nextLearning);
            preferences.UpdateSelections([], nextTeaching, nextLearning);
        });
        Assert.Same(interests, preferences.Interests);
        Assert.Same(priorTeaching, preferences.TeachingSkills);
        Assert.Same(priorLearning, preferences.LearningSkills);
        Assert.Same(goals, preferences.Goals);
        Assert.Same(goal, Assert.Single(preferences.Goals));
        Assert.Equal(id, Assert.Single(preferences.TeachingSkills));
        Assert.Equal(id, Assert.Single(preferences.LearningSkills));
        catalogue.ValidateNewSelections([id], [id]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NullSelectionList_IsRejected(bool teaching)
    {
        var catalogue = new LearningSkillCatalogue([]);
        Assert.Throws<ArgumentNullException>(teaching ? "teachingSkills" : "learningSkills", () =>
            catalogue.ValidateNewSelections(teaching ? null! : [], teaching ? [] : null!));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnumerationFailure_DoesNotChangeCatalogueOrInputIdentity(bool teaching)
    {
        var id = Guid.NewGuid();
        var skill = Active(id);
        var catalogue = new LearningSkillCatalogue([skill]);
        Assert.Throws<InvalidOperationException>(() => catalogue.ValidateNewSelections(
            teaching ? FailingSelections(id) : [id], teaching ? [id] : FailingSelections(id)));
        catalogue.ValidateNewSelections([id], [id]);
        Assert.Equal(id, skill.Id);
        Assert.True(skill.IsApproved);
        Assert.Equal(LearningSkillCatalogueStatus.Active, skill.Status);
    }

    private static IEnumerable<Guid> FailingSelections(Guid id)
    {
        yield return id;
        throw new InvalidOperationException("Synthetic enumeration failure.");
    }

    [Theory]
    [InlineData(LearningSkillCatalogueStatus.Active, false)]
    [InlineData(LearningSkillCatalogueStatus.Retired, true)]
    [InlineData(LearningSkillCatalogueStatus.Superseded, true)]
    [InlineData(LearningSkillCatalogueStatus.Merged, true)]
    public void IneligibleNewSelection_DoesNotRemoveOrRewriteHistoricalReferences(
        LearningSkillCatalogueStatus status, bool approved)
    {
        var id = Guid.NewGuid();
        var preferences = Preferences();
        preferences.UpdateSelections([], [id], [id]);
        var teaching = preferences.TeachingSkills;
        var learning = preferences.LearningSkills;
        var row = LearningSkillSelection.CreateFromPreferences(Guid.NewGuid(), preferences, id,
            LearningSkillSelectionKind.Learning);
        var rowId = row.Id;
        var catalogue = new LearningSkillCatalogue([new Skill(id, approved, status)]);
        Assert.Throws<ArgumentException>(() => catalogue.ValidateNewSelections(teaching, learning));
        Assert.Same(teaching, preferences.TeachingSkills);
        Assert.Same(learning, preferences.LearningSkills);
        Assert.Equal(id, Assert.Single(preferences.TeachingSkills));
        Assert.Equal(id, Assert.Single(preferences.LearningSkills));
        Assert.Equal(rowId, row.Id);
        Assert.Equal(id, row.SkillId);
        Assert.Equal(preferences.LearningProfileId, row.LearningProfileId);
        Assert.Equal(LearningSkillSelectionKind.Learning, row.Kind);
        Assert.Equal("learning", row.Module);
        // This verifies local object preservation, not database retention or administrative history.
    }

    [Theory]
    [InlineData(LearningSkillCatalogueStatus.Superseded)]
    [InlineData(LearningSkillCatalogueStatus.Merged)]
    public void AnotherActiveSkill_DoesNotAutomaticallyReplaceAnIneligibleIdentity(LearningSkillCatalogueStatus status)
    {
        var oldId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var catalogue = new LearningSkillCatalogue([new Skill(oldId, true, status), Active(otherId)]);
        Guid[] candidate = [oldId];
        Assert.Throws<ArgumentException>(() => catalogue.ValidateNewSelections(candidate, []));
        Assert.Equal(oldId, Assert.Single(candidate));
        catalogue.ValidateNewSelections([otherId], []);
        Assert.Throws<ArgumentException>(() => catalogue.ValidateNewSelections([], candidate));
        Assert.Equal(oldId, Assert.Single(candidate));
        // No successor relationship is invented between these independent catalogue entries.
    }

    [Fact]
    public void Catalogue_DefensivelyCopiesCallerCollection()
    {
        var id = Guid.NewGuid();
        var introducedLater = Guid.NewGuid();
        var original = Active(id);
        var input = new List<Skill> { original };
        var catalogue = new LearningSkillCatalogue(input);
        input[0] = new Skill(id, true, LearningSkillCatalogueStatus.Retired);
        input.Add(Active(introducedLater));
        input.Clear();
        catalogue.ValidateNewSelections([id], [id]);
        Assert.Throws<ArgumentException>(() => catalogue.ValidateNewSelections([introducedLater], []));
        Assert.Equal(id, original.Id);
        Assert.Equal(LearningSkillCatalogueStatus.Active, original.Status);
    }

    [Fact]
    public void SeparateSnapshots_DoNotSilentlyChangeEachOthersEligibility()
    {
        var id = Guid.NewGuid();
        var activeSnapshot = new LearningSkillCatalogue([Active(id)]);
        var retiredSnapshot = new LearningSkillCatalogue([new Skill(id, true, LearningSkillCatalogueStatus.Retired)]);
        Assert.Throws<ArgumentException>(() => retiredSnapshot.ValidateNewSelections([id], []));
        activeSnapshot.ValidateNewSelections([id], [id]);
        Assert.Throws<ArgumentException>(() => retiredSnapshot.ValidateNewSelections([], [id]));
        // Snapshots do not establish freshness: a future authoritative reader must do so.
    }

    [Fact]
    public void Validation_DoesNotChangeProfileScopeInterestsGoalsOrAnotherDomainInstance()
    {
        var id = Guid.NewGuid();
        var catalogue = new LearningSkillCatalogue([Active(id)]);
        var preferences = Preferences();
        preferences.UpdateSelections([Guid.NewGuid()], [id], [id]);
        var goal = preferences.CreateGoal("Private test goal");
        var other = Preferences();
        var account = preferences.AccountId;
        var profile = preferences.LearningProfileId;
        var interests = preferences.Interests;
        catalogue.ValidateNewSelections(preferences.TeachingSkills, preferences.LearningSkills);
        Assert.Equal(account, preferences.AccountId);
        Assert.Equal(profile, preferences.LearningProfileId);
        Assert.Equal("learning", preferences.Module);
        Assert.Same(interests, preferences.Interests);
        Assert.Same(goal, Assert.Single(preferences.Goals));
        Assert.Empty(other.Interests);
        Assert.Empty(other.TeachingSkills);
        Assert.Empty(other.LearningSkills);
        Assert.Empty(other.Goals);
        // Catalogue validation receives no account, Hobbies, Social or other-module content.
    }

    [Fact]
    public void DefaultJsonAndDiagnostics_OmitCatalogueFactsWithoutMutatingSource()
    {
        var id = Guid.NewGuid();
        var skill = Active(id);
        var catalogue = new LearningSkillCatalogue([skill]);
        var options = new JsonSerializerOptions();
        var catalogueJson = JsonSerializer.Serialize((object)catalogue, catalogue.GetType(), options);
        var skillJson = JsonSerializer.Serialize((object)skill, skill.GetType(), options);
        Assert.True(catalogueJson == "{}" && skillJson == "{}",
            "Default serialization must omit internal catalogue facts.");
        Assert.True(!(catalogue.ToString() + skill).Contains(id.ToString(), StringComparison.Ordinal),
            "Diagnostics must omit catalogue identities.");
        catalogue.ValidateNewSelections([id], [id]);
        Assert.Equal(id, skill.Id);
        Assert.True(skill.IsApproved);
        Assert.Equal(LearningSkillCatalogueStatus.Active, skill.Status);
    }
}
