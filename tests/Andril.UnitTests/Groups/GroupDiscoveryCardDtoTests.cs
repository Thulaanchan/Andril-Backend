using System.Text.Json;
using Andril.Groups.DTOs;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupDiscoveryCardDtoTests
{
    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public async Task Projection_ContainsOnlySourceBackedFieldsAndIsImmutable(string module, GroupKind kind)
    {
        var scope = new GroupModuleScope(module, kind);
        var actorId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var repository = new RepositoryDouble(new(groupId, scope, "Approved group summary"));
        var actor = new AuthoritativeGroupProfileEvidence(actorId, module, true, false, 25, DateTimeOffset.UtcNow);
        var result = await new GroupDiscoveryService(repository).DiscoverAsync(actor, scope, "", 1);
        Assert.NotNull(result);
        var cards = GroupDiscoveryCardDto.FromAuthorizedResult(result, scope);
        var card = Assert.Single(cards);
        Assert.Equal(groupId, card.Id);
        Assert.Equal(module, card.Module);
        Assert.Equal(kind, card.GroupKind);
        Assert.Equal("Approved group summary", card.Name);
        Assert.All(typeof(GroupDiscoveryCardDto).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.Empty(typeof(GroupDiscoveryCardDto).GetConstructors());
        Assert.Throws<NotSupportedException>(() => ((IList<GroupDiscoveryCardDto>)cards).Clear());
        var json = JsonSerializer.Serialize(card, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "groupKind", "id", "module", "name" },
            document.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray());
        Assert.DoesNotContain(actorId.ToString(), json);
        Assert.DoesNotContain("confidential-evidence-revision", json);
        Assert.Equal("{}", JsonSerializer.Serialize(result));
        Assert.Equal("Group discovery card [private]", card.ToString());
        Assert.Throws<InvalidOperationException>(() => GroupDiscoveryCardDto.FromAuthorizedResult(result,
            module == "social" ? new("learning", GroupKind.Classroom) : new("social", GroupKind.Zone)));
        Assert.Throws<ArgumentNullException>(() => GroupDiscoveryCardDto.FromAuthorizedResult(null!, scope));
        Assert.Throws<ArgumentNullException>(() => GroupDiscoveryCardDto.FromAuthorizedResult(result, null!));
    }

    private sealed class RepositoryDouble(GroupEntry group) : IGroupDiscoveryReadRepository
    {
        public Task<T?> WithAuthorizedReadAsync<T>(GroupDiscoveryQuery query,
            Func<GroupDiscoverySnapshot, CancellationToken, Task<T?>> project, CancellationToken cancellationToken = default) where T : class
            => project(new(query.ActorProfileId, query.Scope, Array.AsReadOnly(new[] {
                new GroupDiscoveryEvidence(group, GroupDiscoveryLifecycle.Active, GroupDiscoveryDecision.Allowed,
                    GroupDiscoveryPrivacy.Private, GroupDiscoveryDecision.Allowed, GroupDiscoveryDecision.Allowed,
                    GroupDiscoveryDecision.Allowed, GroupDiscoveryDecision.Allowed, "confidential-evidence-revision", DateTimeOffset.UtcNow)
            }), "confidential-evidence-revision"), cancellationToken);
        public Task<GroupDiscoveryDecision> RevalidateAsync(GroupDiscoveryQuery query, GroupDiscoverySnapshot snapshot,
            CancellationToken cancellationToken = default) => Task.FromResult(GroupDiscoveryDecision.Allowed);
    }
}
