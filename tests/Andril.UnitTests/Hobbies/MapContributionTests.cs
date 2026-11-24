using System;
using System.Linq;
using System.Reflection;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Xunit;

namespace Andril.UnitTests.Hobbies;

public sealed class MapContributionTests
{
    private static readonly Guid ContributionId = new("11111111-1111-4111-8111-111111111111");
    private static readonly Guid ProfileId = new("22222222-2222-4222-8222-222222222222");
    private static readonly Guid AttractionId = new("33333333-3333-4333-8333-333333333333");
    private static readonly Guid CaptureSessionId = new("44444444-4444-4444-8444-444444444444");
    private static readonly DateTimeOffset SubmittedAtUtc = new(2026, 11, 17, 1, 0, 0, TimeSpan.Zero);
    private static readonly MapContributionLocationEvidence Evidence = new(
        7.1234, 80.5678, 7.1235, 80.5679, 12.5, SubmittedAtUtc.AddSeconds(-30), SubmittedAtUtc.AddSeconds(-5));

    private const string PhotoKey = "map/contributions/capture/photo.jpg";
    private const string Description = "  Synthetic map contribution  ";

    [Fact]
    public void Create_ValidInputs_PreservesValuesAndStartsPendingReview()
    {
        var contribution = CreateContribution();

        Assert.Equal(ContributionId, contribution.ContributionId);
        Assert.Equal(ProfileId, contribution.OwnerHobbiesModuleProfileId);
        Assert.Equal(AttractionId, contribution.AttractionId);
        Assert.Equal(CaptureSessionId, contribution.CaptureSessionId);
        Assert.Equal(PhotoKey, contribution.PrivatePhotoStorageObjectKey);
        Assert.Equal(Description, contribution.Description);
        Assert.Equal(Evidence, contribution.LocationEvidence);
        Assert.Equal(MapContributionModerationStatus.PendingReview, contribution.ModerationStatus);
    }

    [Fact]
    public void Create_EmptyContributionId_IsRejected() =>
        Assert.Throws<ArgumentException>("contributionId", () =>
            MapContribution.Create(Guid.Empty, ProfileId, AttractionId, CaptureSessionId,
                PhotoKey, Description, Evidence, SubmittedAtUtc));

    [Fact]
    public void Create_EmptyOwnerProfileId_IsRejected() =>
        Assert.Throws<ArgumentException>("ownerHobbiesModuleProfileId", () =>
            MapContribution.Create(ContributionId, Guid.Empty, AttractionId, CaptureSessionId,
                PhotoKey, Description, Evidence, SubmittedAtUtc));

    [Fact]
    public void Create_EmptyAttractionId_IsRejected() =>
        Assert.Throws<ArgumentException>("attractionId", () =>
            MapContribution.Create(ContributionId, ProfileId, Guid.Empty, CaptureSessionId,
                PhotoKey, Description, Evidence, SubmittedAtUtc));

    [Fact]
    public void Create_EmptyCaptureSessionId_IsRejected() =>
        Assert.Throws<ArgumentException>("captureSessionId", () =>
            MapContribution.Create(ContributionId, ProfileId, AttractionId, Guid.Empty,
                PhotoKey, Description, Evidence, SubmittedAtUtc));

    [Fact]
    public void Create_NullPhotoKey_IsRejected() =>
        Assert.Throws<ArgumentNullException>("privatePhotoStorageObjectKey", () =>
            CreateContribution(photoKey: null!));

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("http://example.invalid/photo.jpg")]
    [InlineData("https://example.invalid/photo.jpg")]
    [InlineData("  https://example.invalid/photo.jpg  ")]
    [InlineData("//example.invalid/photo.jpg")]
    [InlineData("file:///photos/photo.jpg")]
    [InlineData(@"map\photo.jpg")]
    public void Create_InvalidPhotoKey_IsRejected(string key) =>
        Assert.Throws<ArgumentException>("privatePhotoStorageObjectKey", () =>
            CreateContribution(photoKey: key));

    [Fact]
    public void Create_NullDescription_IsRejected() =>
        Assert.Throws<ArgumentNullException>("description", () =>
            CreateContribution(description: null!));

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("\u2003")]
    public void Create_BlankDescription_IsRejected(string description) =>
        Assert.Throws<ArgumentException>("description", () =>
            CreateContribution(description: description));

    [Fact]
    public void Create_UnicodeDescription_PreservesOriginalText()
    {
        const string description = "  කඳුකර දසුන · மலைக்காட்சி 🗺️  ";
        Assert.Equal(description, CreateContribution(description: description).Description);
    }

    [Fact]
    public void Create_NullEvidence_IsRejected() =>
        Assert.Throws<ArgumentNullException>("locationEvidence", () =>
            MapContribution.Create(ContributionId, ProfileId, AttractionId, CaptureSessionId,
                PhotoKey, Description, null!, SubmittedAtUtc));

    [Fact]
    public void LocationEvidence_CopyWithChangedValues_LeavesContributionEvidenceUnchanged()
    {
        var contribution = CreateContribution();
        var revisedEvidence = contribution.LocationEvidence with { HorizontalAccuracyMetres = 20 };

        Assert.NotEqual(revisedEvidence, contribution.LocationEvidence);
        Assert.Equal(Evidence, contribution.LocationEvidence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(330)]
    [InlineData(-420)]
    public void Create_SubmissionTime_PreservesInstantInUtc(int offsetMinutes)
    {
        var input = SubmittedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMinutes));
        var contribution = CreateContribution(submittedAt: input);

        Assert.Equal(input, contribution.SubmittedAtUtc);
        Assert.Equal(SubmittedAtUtc, contribution.SubmittedAtUtc);
        Assert.Equal(TimeSpan.Zero, contribution.SubmittedAtUtc.Offset);
    }

    [Fact]
    public void DomainState_HasOneReadOnlyPhotoReferenceAndNoPublicMutation()
    {
        var type = typeof(MapContribution);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.True(type.IsSealed);
        Assert.Empty(type.GetConstructors());
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
        Assert.All(properties, property => Assert.False(property.SetMethod?.IsPublic == true, property.Name));
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => !method.IsSpecialName);

        var photo = Assert.Single(properties, property => property.Name.Contains("Photo", StringComparison.Ordinal));
        Assert.Equal(nameof(MapContribution.PrivatePhotoStorageObjectKey), photo.Name);
        Assert.Equal(typeof(string), photo.PropertyType);

        var factory = Assert.Single(type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
        Assert.Equal(nameof(MapContribution.Create), factory.Name);
        Assert.Equal(
            new[] { typeof(Guid), typeof(Guid), typeof(Guid), typeof(Guid), typeof(string), typeof(string),
                typeof(MapContributionLocationEvidence), typeof(DateTimeOffset) },
            factory.GetParameters().Select(parameter => parameter.ParameterType));
    }

    private static MapContribution CreateContribution(
        string photoKey = PhotoKey,
        string description = Description,
        DateTimeOffset? submittedAt = null) =>
        MapContribution.Create(ContributionId, ProfileId, AttractionId, CaptureSessionId,
            photoKey, description, Evidence, submittedAt ?? SubmittedAtUtc);
}
