using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Groups;

public sealed record GroupSettings(string Name, string? Description, bool IsDiscoverable, string Visibility = "private");
public sealed record StoredGroup(Guid Id, string Name, string? Description, bool IsDiscoverable, string Visibility, long Revision);

public sealed class PostgresGroupEntryRepository(GroupsPostgresScope execution) : IGroupEntryRepository
{
    public async Task<GroupEntry?> GetGroupAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default)
    {
        var result = await GetDetailsAsync(groupId, scope, cancellationToken);
        return result is null ? null : new GroupEntry(result.Id, scope, result.Name);
    }

    public async Task<bool> ExistsAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) =>
        await GetGroupAsync(groupId, scope, cancellationToken) is not null;

    public Task<StoredGroup?> GetDetailsAsync(Guid groupId, GroupModuleScope scope, CancellationToken token = default) =>
        execution.ExecuteAsync(scope.Module, (db, actor, ct) => db.Database.SqlQuery<StoredGroup>($"""
            SELECT id AS "Id", name AS "Name", description AS "Description", is_discoverable AS "IsDiscoverable",
                visibility AS "Visibility", revision AS "Revision"
            FROM andril_groups.read_group_details({actor}, {scope.Module}, {groupId})
            """).SingleOrDefaultAsync(ct), token);

    public Task<StoredGroup> CreateAsync(GroupModuleScope scope, GroupSettings settings, CancellationToken token = default)
    {
        settings = Validate(settings);
        var id = Guid.NewGuid();
        return execution.ExecuteAsync(scope.Module, (db, actor, ct) => db.Database.SqlQuery<StoredGroup>($"""
            SELECT id AS "Id", name AS "Name", description AS "Description", is_discoverable AS "IsDiscoverable",
                visibility AS "Visibility", revision AS "Revision"
            FROM andril_groups.create_group({actor}, {scope.Module}, {(int)scope.GroupKind}, {id}, {settings.Name},
                {settings.Description}, {settings.Visibility}, {settings.IsDiscoverable})
            """).SingleAsync(ct), token);
    }

    public Task<StoredGroup?> UpdateAsync(Guid id, GroupModuleScope scope, GroupSettings settings, long expectedRevision, CancellationToken token = default)
    {
        settings = Validate(settings);
        if (id == Guid.Empty || expectedRevision < 1) throw new ArgumentException("Valid group identity and revision are required.");
        return execution.ExecuteAsync(scope.Module, (db, actor, ct) => db.Database.SqlQuery<StoredGroup>($"""
            SELECT id AS "Id", name AS "Name", description AS "Description", is_discoverable AS "IsDiscoverable",
                visibility AS "Visibility", revision AS "Revision"
            FROM andril_groups.update_group({actor}, {scope.Module}, {id}, {settings.Name}, {settings.Description},
                {settings.Visibility}, {settings.IsDiscoverable}, {expectedRevision})
            """).SingleOrDefaultAsync(ct), token);
    }

    private static GroupSettings Validate(GroupSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Trim().Length > 160 || value.Name.Any(char.IsControl)
            || value.Description?.Length > 2000 || value.Visibility is not ("private" or "public"))
            throw new ArgumentException("Invalid group configuration.", nameof(value));
        return value with { Name = value.Name.Trim() };
    }
}
