
using System;
using System.Linq;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Policies;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>
/// Tests pure metadata projection, not live authorization or SQL.
/// </summary>
public sealed class AiConversationReadPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void Project_MapsExactMetadata(AiModule module)
    {
        var actor = Owner(module);
        var row = Create(actor);
        var result = Assert.IsType<AiStoredConversation>(
            Read(actor, row));

        Assert.Equal(row.Id, result.ConversationId);
        Assert.Equal(actor.UserId, result.AccountId);
        Assert.Equal(actor.ModuleProfileId, result.ModuleProfileId);
        Assert.Equal(module, result.Module);
        Assert.Equal(row.CreatedAtUtc, result.CreatedAtUtc);
        Assert.Equal(row.LastSavedUserMessageAtUtc,
            result.LastSavedUserMessageAtUtc);
        Assert.Equal(row.ExpiresAtUtc, result.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void InvalidActor_ExposesNothing(int defect)
    {
        var owner = Owner();
        var row = Create(owner);

        AiAuthorizedActor? invalid = defect switch
        {
            0 => null,
            1 => owner with { UserId = Guid.Empty },
            2 => owner with { ModuleProfileId = Guid.Empty },
            3 => owner with { Module = (AiModule)0 },
            _ => owner with { Module = (AiModule)99 }
        };

        Assert.Null(Read(invalid, row));
        Assert.Empty(History(invalid, row));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DifferentIdentity_ExposesNothing(int mismatch)
    {
        var owner = Owner();
        var row = Create(owner);

        var other = mismatch switch
        {
            0 => owner with { UserId = Guid.NewGuid() },
            1 => owner with { ModuleProfileId = Guid.NewGuid() },
            _ => owner with { Module = AiModule.Hobbies }
        };

        Assert.Null(Read(other, row));
        Assert.Empty(History(other, row));
    }

    [Fact]
    public void Project_MissingRowOrWrongRequestedId_ReturnsNull()
    {
        var actor = Owner();
        var row = Create(actor);

        Assert.Null(AiConversationReadPolicy.TryProject(
            actor, row.Id, null, Now));

        Assert.Null(AiConversationReadPolicy.TryProject(
            actor, Guid.Empty, row, Now));

        Assert.Null(AiConversationReadPolicy.TryProject(
            actor, Guid.NewGuid(), row, Now));
    }

    [Fact]
    public void DefaultClock_ExposesNothing()
    {
        var actor = Owner();
        var row = Create(actor);

        Assert.Null(Read(actor, row, default(DateTimeOffset)));

        Assert.Empty(AiConversationReadPolicy.ProjectHistory(
            actor, new[] { row }, default, 1));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void Project_EnforcesExactExpiryBoundary(
        int ticks, bool visible)
    {
        var actor = Owner();
        var row = Create(actor);
        var instant = row.ExpiresAtUtc.AddTicks(ticks);

        Assert.Equal(visible,
            Read(actor, row, instant) is not null);
    }

    [Fact]
    public void Project_NormalizesClockOffset()
    {
        var actor = Owner();
        var row = Create(actor);

        var localDeadline = row.ExpiresAtUtc.ToOffset(
            TimeSpan.FromMinutes(330));

        Assert.NotNull(Read(
            actor, row, localDeadline.AddTicks(-1)));

        Assert.Null(Read(actor, row, localDeadline));
    }

    [Fact]
    public void Project_RejectsFutureLastSavedTimestamp()
    {
        var actor = Owner();
        var row = Create(actor);

        row.ApplySavedUserMessage(Now.AddTicks(1));

        Assert.Null(Read(actor, row));
        Assert.Empty(History(actor, row));
    }

    [Fact]
    public void Project_RenewedConversationUsesNewExpiry()
    {
        var actor = Owner();
        var row = Create(actor);
        var originalExpiry = row.ExpiresAtUtc;

        row.ApplySavedUserMessage(
            row.CreatedAtUtc.AddDays(2));

        Assert.NotNull(Read(actor, row, originalExpiry));
        Assert.Null(Read(actor, row, row.ExpiresAtUtc));
    }

    [Fact]
    public void DeletedConversation_ImmediatelyHidden()
    {
        var actor = Owner();
        var row = Create(actor);

        row.MarkDeleted(Now);

        Assert.Null(Read(actor, row));
        Assert.Empty(History(actor, row));
    }

    // Simulate malformed ORM rows in tests only.
    [Theory]
    [InlineData("Id")]
    [InlineData("AccountId")]
    [InlineData("ModuleProfileId")]
    [InlineData("CreateOperationId")]
    [InlineData("Module")]
    [InlineData("CreatedAtUtc")]
    [InlineData("NonUtcCreatedAt")]
    [InlineData("LastSavedUserMessageAtUtc")]
    [InlineData("NonUtcLastSavedAt")]
    [InlineData("ExpiresAtUtc")]
    [InlineData("NonUtcExpiry")]
    public void MalformedRow_ExposesNothing(string defect)
    {
        var actor = Owner();
        var row = Create(actor);

        switch (defect)
        {
            case "Module":
                Corrupt(row, "Module", (AiModule)99);
                break;

            case "CreatedAtUtc":
                Corrupt(row, defect, default(DateTimeOffset));
                break;

            case "NonUtcCreatedAt":
                Corrupt(row, "CreatedAtUtc",
                    row.CreatedAtUtc.ToOffset(TimeSpan.FromHours(1)));
                break;

            case "LastSavedUserMessageAtUtc":
                Corrupt(row, defect, row.CreatedAtUtc.AddTicks(-1));
                break;

            case "NonUtcLastSavedAt":
                Corrupt(row, "LastSavedUserMessageAtUtc",
                    row.LastSavedUserMessageAtUtc.ToOffset(
                        TimeSpan.FromHours(1)));
                break;

            case "ExpiresAtUtc":
                Corrupt(row, defect, row.ExpiresAtUtc.AddTicks(1));
                break;

            case "NonUtcExpiry":
                Corrupt(row, "ExpiresAtUtc",
                    row.ExpiresAtUtc.ToOffset(TimeSpan.FromHours(1)));
                break;

            default:
                Corrupt(row, defect, Guid.Empty);
                break;
        }

        Assert.Null(Read(actor, row));
        Assert.Empty(History(actor, row));
    }

    [Fact]
    public void Project_OverflowingExpiryCalculationFailsClosed()
    {
        var actor = Owner();
        var row = Create(actor);

        var nearMaximum = DateTimeOffset.MaxValue.AddDays(-1);

        Corrupt(row, "LastSavedUserMessageAtUtc", nearMaximum);
        Corrupt(row, "ExpiresAtUtc", DateTimeOffset.MaxValue);

        Assert.Null(Read(actor, row, nearMaximum));

        Assert.Empty(AiConversationReadPolicy.ProjectHistory(
            actor, new[] { row }, nearMaximum, 1));
    }

    [Fact]
    public void History_FiltersAllInaccessibleRows()
    {
        var actor = Owner();
        var valid = Create(actor);
        var expired = Create(actor, Now.AddDays(-14));

        var deleted = Create(actor);
        deleted.MarkDeleted(Now);

        var malformed = Create(actor);
        Corrupt(malformed, "CreateOperationId", Guid.Empty);

        var future = Create(actor, Now.AddTicks(1));

        var result = History(
            actor,
            expired,
            Create(Owner()),
            deleted,
            Create(actor with { ModuleProfileId = Guid.NewGuid() }),
            Create(actor with { Module = AiModule.Hobbies }),
            malformed,
            future,
            null!,
            valid);

        Assert.Equal(valid.Id,
            Assert.Single(result).ConversationId);
    }

    [Fact]
    public void History_SortsByActivityThenCreationThenId()
    {
        var actor = Owner();

        var older = Create(actor, Now.AddDays(-3));
        var newer = Create(actor, Now.AddDays(-2));
        var tied = Create(actor, Now.AddDays(-2));
        var inactive = Create(actor, Now.AddDays(-1));

        foreach (var row in new[] { older, newer, tied })
            row.ApplySavedUserMessage(Now);

        var expected = new[] { newer.Id, tied.Id }
            .OrderBy(id => id)
            .Concat(new[] { older.Id, inactive.Id })
            .ToArray();

        Assert.Equal(expected,
            History(actor, inactive, older, tied, newer)
                .Select(r => r.ConversationId)
                .ToArray());
    }

    [Fact]
    public void History_AppliesLimitAfterFilteringAndSorting()
    {
        var actor = Owner();

        var older = Create(actor, Now.AddDays(-3));
        var recent = Create(actor);
        var expired = Create(actor, Now.AddDays(-15));

        var result = AiConversationReadPolicy.ProjectHistory(
            actor, new[] { expired, older, recent }, Now, 1);

        Assert.Equal(recent.Id,
            Assert.Single(result).ConversationId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(101)]
    public void History_InvalidLimitThrows(int limit)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            AiConversationReadPolicy.ProjectHistory(
                Owner(),
                Array.Empty<AiConversationEntity>(),
                Now,
                limit));

        Assert.Equal("limit", error.ParamName);
    }

    [Fact]
    public void History_AcceptsExactlyOneHundredCandidates()
    {
        var actor = Owner();

        var rows = Enumerable.Range(0, 100)
            .Select(_ => Create(actor))
            .ToArray();

        Assert.Equal(100, History(actor, rows).Count);
    }

    [Fact]
    public void History_RejectsExcessCandidates()
    {
        var actor = Owner();

        var rows = Enumerable.Range(0, 101)
            .Select(_ => Create(actor))
            .ToArray();

        Assert.Throws<InvalidOperationException>(() =>
            History(actor, rows));
    }

    [Fact]
    public void History_RejectsDuplicateVisibleRowsEvenBeyondLimit()
    {
        var actor = Owner();
        var row = Create(actor);

        Assert.Throws<InvalidOperationException>(() =>
            AiConversationReadPolicy.ProjectHistory(
                actor, new[] { row, row }, Now, 1));
    }

    [Fact]
    public void History_ExcludesDuplicateInaccessibleRows()
    {
        var actor = Owner();
        var foreign = Create(Owner());

        Assert.Empty(History(actor, foreign, foreign));
    }

    [Fact]
    public void History_EmptyInputReturnsEmpty()
    {
        Assert.Empty(History(Owner()));
    }

    [Fact]
    public void History_NullCandidatesThrows()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AiConversationReadPolicy.ProjectHistory(
                Owner(), null!, Now, 1));
    }

    [Fact]
    public void Reads_DoNotRenewOrMutateConversation()
    {
        var actor = Owner();
        var row = Create(actor);
        var before = Read(actor, row);

        Assert.NotNull(before);

        Assert.Equal(before,
            Assert.Single(History(actor, row)));

        Assert.Equal(before,
            Read(actor, row, Now.AddHours(1)));

        Assert.Equal(before!.LastSavedUserMessageAtUtc,
            row.LastSavedUserMessageAtUtc);

        Assert.Equal(before.ExpiresAtUtc, row.ExpiresAtUtc);
        Assert.Null(row.DeletedAtUtc);
    }

    private static AiAuthorizedActor Owner(
        AiModule module = AiModule.Learning) =>
        new(Guid.NewGuid(), Guid.NewGuid(), module);

    private static AiConversationEntity Create(
        AiAuthorizedActor actor,
        DateTimeOffset? created = null) =>
        AiConversationEntity.CreateForFirstUserMessage(
            actor,
            Guid.NewGuid(),
            created ?? Now.AddDays(-1));

    private static AiStoredConversation? Read(
        AiAuthorizedActor? actor,
        AiConversationEntity row,
        DateTimeOffset? now = null) =>
        AiConversationReadPolicy.TryProject(
            actor, row.Id, row, now ?? Now);

    private static System.Collections.Generic
        .IReadOnlyList<AiStoredConversation> History(
            AiAuthorizedActor? actor,
            params AiConversationEntity[] rows) =>
        AiConversationReadPolicy.ProjectHistory(
            actor, rows, Now, 100);

    private static void Corrupt(
        AiConversationEntity row,
        string property,
        object value) =>
        typeof(AiConversationEntity)
            .GetProperty(property)!
            .GetSetMethod(nonPublic: true)!
            .Invoke(row, new[] { value });
}
