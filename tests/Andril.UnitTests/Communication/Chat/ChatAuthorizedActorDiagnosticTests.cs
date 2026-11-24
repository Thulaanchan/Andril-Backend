using System.Globalization;
using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Xunit;

namespace Andril.UnitTests.Communication.Chat;

public sealed class ChatAuthorizedActorDiagnosticTests
{
    private static readonly ChatModule[] Modules = [ChatModule.Learning, ChatModule.Hobbies, ChatModule.Social];
    private static readonly Guid ReplacementId = Guid.Parse("90000000-0000-4000-8000-000000000001");

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public void OrdinaryFormatting_ShouldExposeOnlyTheSafeTypeName(ChatModule module)
    {
        var actor = Create(module);
        object actorObject = actor;
        string[] outputs = [actor.ToString(), $"{actor}", string.Format(CultureInfo.InvariantCulture, "{0}", actor),
            actorObject.ToString()!, $"{actorObject}", string.Format(CultureInfo.InvariantCulture, "{0}", actorObject)];

        foreach (var output in outputs)
        {
            Assert.Equal(nameof(ChatAuthorizedActor), output);
            foreach (var otherModule in Modules)
            {
                var other = Create(otherModule);
                foreach (var identity in new[] { other.VerifiedSubjectId, other.AccountId, other.ModuleProfileId, other.ConversationId })
                {
                    Assert.DoesNotContain(identity.ToString("D"), output, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(identity.ToString("N"), output, StringComparison.OrdinalIgnoreCase);
                }
                Assert.DoesNotContain(otherModule.ToString(), output, StringComparison.Ordinal);
            }
            foreach (var property in new[] { nameof(actor.VerifiedSubjectId), nameof(actor.AccountId), nameof(actor.ModuleProfileId),
                nameof(actor.Module), nameof(actor.ConversationId) })
            {
                Assert.DoesNotContain(property, output, StringComparison.Ordinal);
            }
        }

        AssertOriginalValues(actor, module);
    }

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public void PropertiesAndDeconstruction_ShouldRemainUnchanged(ChatModule module)
    {
        var actor = Create(module);
        AssertOriginalValues(actor, module);

        var (subject, account, profile, actualModule, conversation) = actor;
        Assert.Equal(Identity(module, 1), subject);
        Assert.Equal(Identity(module, 2), account);
        Assert.Equal(Identity(module, 3), profile);
        Assert.Equal(module, actualModule);
        Assert.Equal(Identity(module, 4), conversation);
    }

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public void EqualRecordsAndCopies_ShouldRetainEqualityAndHashCode(ChatModule module)
    {
        var actor = Create(module);
        var equal = Create(module);
        var copy = actor with { };

        Assert.NotSame(actor, equal);
        Assert.NotSame(actor, copy);
        Assert.Equal(actor, equal);
        Assert.Equal(actor, copy);
        Assert.Equal(actor.GetHashCode(), equal.GetHashCode());
        Assert.Equal(actor.GetHashCode(), copy.GetHashCode());
        Assert.Contains(equal, new HashSet<ChatAuthorizedActor> { actor });
        AssertOriginalValues(copy, module);
        Assert.Equal(nameof(ChatAuthorizedActor), copy.ToString());
    }

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public void ChangingAnyPositionalMember_ShouldStillChangeEquality(ChatModule module)
    {
        var actor = Create(module);
        ChatAuthorizedActor[] different = [actor with { VerifiedSubjectId = ReplacementId }, actor with { AccountId = ReplacementId },
            actor with { ModuleProfileId = ReplacementId }, actor with { ConversationId = ReplacementId },
            actor with { Module = module == ChatModule.Social ? ChatModule.Learning : ChatModule.Social }];

        foreach (var candidate in different)
        {
            Assert.NotEqual(actor, candidate);
            Assert.Equal(nameof(ChatAuthorizedActor), candidate.ToString());
        }
        AssertOriginalValues(actor, module);
    }

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public void WithExpression_ShouldOnlyChangeTheRequestedMember(ChatModule module)
    {
        var actor = Create(module);
        var copy = actor with { ConversationId = ReplacementId };

        Assert.Equal(actor.VerifiedSubjectId, copy.VerifiedSubjectId);
        Assert.Equal(actor.AccountId, copy.AccountId);
        Assert.Equal(actor.ModuleProfileId, copy.ModuleProfileId);
        Assert.Equal(actor.Module, copy.Module);
        Assert.Equal(ReplacementId, copy.ConversationId);
        AssertOriginalValues(actor, module);
        Assert.Equal(nameof(ChatAuthorizedActor), copy.ToString());
    }

    [Theory]
    [InlineData(ChatModule.Learning)]
    [InlineData(ChatModule.Hobbies)]
    [InlineData(ChatModule.Social)]
    public async Task Formatting_ShouldNotChangeFailClosedAuthorization(ChatModule module)
    {
        var actor = Create(module);
        IChatAccessAuthorizer fallback = new DenyAllChatAccessAuthorizer();
        IChatAccessAuthorizer unresolved = new ChatConversationAccessPolicy(null, null);

        Assert.Null(await fallback.AuthorizeConversationAsync(module, actor.ConversationId));
        Assert.Null(await unresolved.AuthorizeConversationAsync(module, actor.ConversationId));
        Assert.Equal(nameof(ChatAuthorizedActor), $"{actor}");
        Assert.Null(await fallback.AuthorizeConversationAsync(module, actor.ConversationId));
        Assert.Null(await unresolved.AuthorizeConversationAsync(module, actor.ConversationId));
        AssertOriginalValues(actor, module);
    }

    private static ChatAuthorizedActor Create(ChatModule module) =>
        new(Identity(module, 1), Identity(module, 2), Identity(module, 3), module, Identity(module, 4));

    // Synthetic fixtures only; these GUID shapes carry no identity or authorization meaning.
    private static Guid Identity(ChatModule module, int field) =>
        Guid.ParseExact(FormattableString.Invariant($"{(int)module:00000000}-0000-4000-8000-{field:000000000000}"), "D");

    private static void AssertOriginalValues(ChatAuthorizedActor actor, ChatModule module)
    {
        Assert.Equal(Identity(module, 1), actor.VerifiedSubjectId);
        Assert.Equal(Identity(module, 2), actor.AccountId);
        Assert.Equal(Identity(module, 3), actor.ModuleProfileId);
        Assert.Equal(module, actor.Module);
        Assert.Equal(Identity(module, 4), actor.ConversationId);
    }
}
