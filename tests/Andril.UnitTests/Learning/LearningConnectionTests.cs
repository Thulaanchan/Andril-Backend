using System.Text.Json;
using Andril.Learning.Activities;
using Andril.Learning.Connections;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class LearningConnectionTests
{
    private readonly LearningActivityMember sender = new(Guid.NewGuid(), Guid.NewGuid()), recipient = new(Guid.NewGuid(), Guid.NewGuid());
    [Fact]
    public void RequestIsPendingUntilExplicitRecipientAcceptance()
    {
        var c = LearningConnection.Request(Guid.NewGuid(), sender, recipient); Assert.Equal("learning", c.Module);
        Assert.Equal(LearningConnectionStatus.Pending, c.Status);
        Assert.Throws<UnauthorizedAccessException>(() => c.Accept(sender));
        c.Accept(recipient); Assert.Equal(LearningConnectionStatus.Accepted, c.Status);
        Assert.Throws<InvalidOperationException>(() => c.Accept(recipient));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void EitherAcceptedParticipantMayDisconnect(bool bySender)
    {
        var c = LearningConnection.Request(Guid.NewGuid(), sender, recipient); c.Accept(recipient);
        c.Disconnect(bySender ? sender : recipient); Assert.Equal(LearningConnectionStatus.Disconnected, c.Status);
        Assert.Throws<InvalidOperationException>(() => c.Disconnect(sender));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void OnlyDesignatedParticipantDeclinesOrCancels(bool decline)
    {
        var c = LearningConnection.Request(Guid.NewGuid(), sender, recipient);
        Assert.Throws<UnauthorizedAccessException>(() => { if (decline) c.Decline(sender); else c.Cancel(recipient); });
        if (decline) c.Decline(recipient); else c.Cancel(sender);
        Assert.Equal(decline ? LearningConnectionStatus.Declined : LearningConnectionStatus.Cancelled, c.Status);
        Assert.Throws<InvalidOperationException>(() => c.Accept(recipient));
    }
    [Fact]
    public void SelfEmptyAndForeignActorsCannotEstablishOrEndConnections()
    {
        Assert.Throws<ArgumentException>(() => LearningConnection.Request(Guid.NewGuid(), sender, sender));
        Assert.Throws<ArgumentException>(() => LearningConnection.Request(Guid.Empty, sender, recipient));
        Assert.Throws<ArgumentException>(() => LearningConnection.Request(Guid.NewGuid(), sender, recipient with { ProfileId = sender.ProfileId }));
        var c = LearningConnection.Request(Guid.NewGuid(), sender, recipient);
        Assert.Throws<InvalidOperationException>(() => c.Disconnect(sender));
        Assert.Throws<UnauthorizedAccessException>(() => c.Disconnect(new(Guid.NewGuid(), Guid.NewGuid())));
        Assert.DoesNotContain(sender.AccountId.ToString(), c.ToString());
    }
    [Fact]
    public void ConnectionReplayPersistsExplicitAcceptanceAndDisconnectionWithoutInferringOtherRights()
    {
        var s = LearningActivityState.Create(LearningActivityKind.Connection, sender, [sender, recipient], new([recipient.ProfileId]));
        var revision = new LearningActivityRevision(Guid.NewGuid(), 1);
        s.Apply(recipient, new(LearningActivityAction.Accept, revision));
        s = JsonSerializer.Deserialize<LearningActivityState>(JsonSerializer.Serialize(s))!;
        Assert.Equal("Accepted", s.Project(revision).Status);
        s.Apply(sender, new(LearningActivityAction.Disconnect, revision)); Assert.Equal("Disconnected", s.Project(revision).Status);
        Assert.Empty(s.Project(revision).Goals); Assert.Empty(s.Project(revision).Commitments);
    }
}
