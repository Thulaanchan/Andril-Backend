using System.Reflection;
using Andril.Learning.Growth;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class LearningGrowthTests
{
    private static LearningGrowthEvidence Create() => LearningGrowthEvidence.RecordSelfReport(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()]);

    [Fact]
    public void SelfReport_PreservesExactIdentitiesAndSkills_WithPrivateLearningProvenance()
    {
        var id = Guid.NewGuid(); var account = Guid.NewGuid(); var profile = Guid.NewGuid();
        var skills = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var evidence = LearningGrowthEvidence.RecordSelfReport(id, account, profile, skills);
        Assert.Equal(id, evidence.Id); Assert.Equal(account, evidence.AccountId); Assert.Equal(profile, evidence.LearningProfileId);
        Assert.Equal("learning", evidence.Module); Assert.True(evidence.IsPrivate);
        Assert.Equal(LearningEvidenceKind.SelfReported, evidence.Kind); Assert.Equal(skills, evidence.SkillIds);
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("account")]
    [InlineData("profile")]
    public void EmptyIdentity_IsRejectedWithoutMutatingExistingEvidence(string invalid)
    {
        var existing = Create(); var skills = existing.SkillIds;
        Assert.Throws<ArgumentException>(() => LearningGrowthEvidence.RecordSelfReport(
            invalid == "evidence" ? Guid.Empty : existing.Id, invalid == "account" ? Guid.Empty : existing.AccountId,
            invalid == "profile" ? Guid.Empty : existing.LearningProfileId, existing.SkillIds));
        Assert.Same(skills, existing.SkillIds); Assert.Equal(LearningEvidenceKind.SelfReported, existing.Kind); Assert.True(existing.IsPrivate);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty-id")]
    [InlineData("duplicate")]
    [InlineData("enumeration")]
    public void InvalidAssociationInput_IsRejectedWithoutChangingExistingRecord(string invalid)
    {
        var evidence = Create(); var snapshot = evidence.SkillIds; var id = Guid.NewGuid();
        if (invalid == "null") Assert.Throws<ArgumentNullException>(() => Record(null!));
        else if (invalid == "enumeration") Assert.Throws<InvalidOperationException>(() => Record(FailingSkills()));
        else Assert.Throws<ArgumentException>(() => Record(invalid == "empty-id" ? new[] { Guid.Empty } : new[] { id, id }));
        Assert.Same(snapshot, evidence.SkillIds); Assert.Equal(2, evidence.SkillIds.Count); Assert.True(evidence.IsPrivate);
        LearningGrowthEvidence Record(IEnumerable<Guid> values) => LearningGrowthEvidence.RecordSelfReport(
            evidence.Id, evidence.AccountId, evidence.LearningProfileId, values);
    }

    private static IEnumerable<Guid> FailingSkills()
    {
        yield return Guid.NewGuid();
        throw new InvalidOperationException("Synthetic association enumeration failure.");
    }

    [Fact]
    public void NoAssociationMinimumOrProficiencyInferenceIsInvented()
    {
        var evidence = LearningGrowthEvidence.RecordSelfReport(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), []);
        Assert.Empty(evidence.SkillIds); Assert.Equal(LearningEvidenceKind.SelfReported, evidence.Kind);
        Assert.Equal(new[] { "AccountId", "Id", "IsPrivate", "Kind", "LearningProfileId", "Module", "SkillIds" },
            typeof(LearningGrowthEvidence).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        // No score, XP, skill-gap verdict or proof of lacking a skill is produced for an empty link set.
    }

    [Fact]
    public void SkillAssociations_AreCopiedAndCannotBeMutatedThroughTheReturnedCollection()
    {
        var input = new[] { Guid.NewGuid() }; var expected = input[0];
        var evidence = LearningGrowthEvidence.RecordSelfReport(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), input);
        input[0] = Guid.NewGuid(); Assert.Equal(expected, Assert.Single(evidence.SkillIds));
        var list = (IList<Guid>)evidence.SkillIds;
        Assert.Throws<NotSupportedException>(() => list.Add(Guid.NewGuid()));
        Assert.Throws<NotSupportedException>(() => list[0] = Guid.NewGuid());
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Equal(expected, Assert.Single(evidence.SkillIds));
    }

    [Fact]
    public void VerifiedKindIsConceptualOnly_WithNoPublicConstructionAssignmentOrPromotionPath()
    {
        var type = typeof(LearningGrowthEvidence);
        Assert.True(type.IsSealed); Assert.Empty(type.GetConstructors()); Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
        Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
        var factories = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.Equal("RecordSelfReport", Assert.Single(factories).Name);
        Assert.Equal(new[] { typeof(Guid), typeof(Guid), typeof(Guid), typeof(IEnumerable<Guid>) }, factories[0].GetParameters().Select(p => p.ParameterType));
        Assert.Equal(new[] { "ToString" }, type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName).Select(method => method.Name));
        Assert.NotEqual(LearningEvidenceKind.Verified, Create().Kind);
        // No XP, client verification flag, credential or mentorship status can be supplied as authority.
    }

    [Fact]
    public void AcceptedMentorship_DoesNotVerifyEvidenceOrChangePrivacyOrSkills()
    {
        var evidence = Create(); var skills = evidence.SkillIds;
        var mentor = new LearningMentorshipParticipant(Guid.NewGuid(), Guid.NewGuid());
        var learner = new LearningMentorshipParticipant(evidence.AccountId, evidence.LearningProfileId);
        var offer = LearningMentorshipOffer.Create(Guid.NewGuid(), mentor);
        var request = LearningMentorship.Request(Guid.NewGuid(), learner, offer); request.Accept(mentor);
        Assert.True(request.IsActive); Assert.Equal(LearningEvidenceKind.SelfReported, evidence.Kind);
        Assert.True(evidence.IsPrivate); Assert.Same(skills, evidence.SkillIds);
    }

    [Fact]
    public void SameUnverifiedOwnerReferences_DoNotGrantAccessOrCreateSharedMutableState()
    {
        var evidence = Create();
        var other = LearningGrowthEvidence.RecordSelfReport(evidence.Id, evidence.AccountId, evidence.LearningProfileId, []);
        Assert.Empty(other.SkillIds); Assert.Equal(2, evidence.SkillIds.Count);
        // IDs identify intended ownership only; neither construction authenticates either operation.
    }

    [Fact]
    public void Diagnostics_OmitOwnerEvidenceAndSkillIdentities()
    {
        var evidence = Create();
        foreach (var id in evidence.SkillIds.Concat(new[] { evidence.Id, evidence.AccountId, evidence.LearningProfileId }))
            Assert.DoesNotContain(id.ToString(), evidence.ToString());
    }
}
