using Andril.Groups.DTOs;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Andril.Persistence.Repositories.Groups;

namespace Andril.API.Authorization.Groups;

public sealed class GroupManagementService(
    PostgresGroupEntryRepository groups,
    PostgresGroupHostRequests hostRequests,
    IGroupDiscoveryAccessService discovery,
    IGroupPersonalParticipationAccessService participation,
    GroupMembershipOperationService membership)
{
    public const int MaximumResults = 100;

    public async Task<GroupDetailsDto> CreateAsync(GroupModuleScope scope, GroupSettings settings, CancellationToken token)
        => Project(await groups.CreateAsync(scope, settings, token), scope);

    public async Task<GroupDetailsDto?> ReadAsync(Guid group, GroupModuleScope scope, CancellationToken token)
        => await groups.GetDetailsAsync(group, scope, token) is { } result ? Project(result, scope) : null;

    public async Task<GroupDetailsDto?> UpdateAsync(Guid group, GroupModuleScope scope, GroupSettings settings, long revision, CancellationToken token)
        => await groups.UpdateAsync(group, scope, settings, revision, token) is { } result ? Project(result, scope) : null;

    public Task<IReadOnlyList<GroupDiscoveryCardDto>?> DiscoverAsync(GroupModuleScope scope, string search, int maximum, CancellationToken token)
        => discovery.DiscoverAsync(scope, search, maximum, token);

    public Task<GroupPersonalParticipationDto?> GetOwnStatusAsync(Guid group, GroupModuleScope scope, CancellationToken token)
        => participation.GetStatusAsync(scope, group, token);

    public async Task<IReadOnlyList<GroupPendingRequestDto>> ReadPendingAsync(Guid group, GroupModuleScope scope, int maximum, CancellationToken token)
        => (await hostRequests.ReadAsync(group, scope, null, maximum, token))
            .Select(request => new GroupPendingRequestDto(request.Id, request.RequestedAt)).ToArray();

    public async Task<GroupJoinOperationResult> DecideAsync(Guid group, GroupModuleScope scope, Guid requestId, bool approve, CancellationToken token)
    {
        var request = (await hostRequests.ReadAsync(group, scope, requestId, 1, token)).SingleOrDefault();
        return request is null ? GroupJoinOperationResult.Failed() :
            await membership.ProcessJoinRequestAtRevisionAsync(group, scope, request.ApplicantId, approve, request.Revision, token);
    }

    private static GroupDetailsDto Project(StoredGroup group, GroupModuleScope scope) =>
        new(group.Id, scope.Module, scope.GroupKind, group.Name, group.Description, group.IsDiscoverable, group.Visibility, group.Revision);
}

public sealed record GroupDetailsDto(Guid Id, string Module, GroupKind GroupKind, string Name, string? Description,
    bool IsDiscoverable, string Visibility, long Revision);

public sealed record GroupPendingRequestDto(Guid RequestId, DateTimeOffset RequestedAtUtc);
