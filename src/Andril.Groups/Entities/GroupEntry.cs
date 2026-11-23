using System;
using System.Text.Json.Serialization;

namespace Andril.Groups.Entities;

/// <summary>
/// Represents private group identity and validated module scope (CF-07 / CF-11).
/// Does not authorize group creation, listing, discoverability, or member access.
/// </summary>
/// <remarks>
/// <para>
/// <b>Security and Privacy Boundaries:</b><br />
/// <list type="bullet">
/// <item><description>Raw domain entity represents internal group identity and scope.</description></item>
/// <item><description>All properties are protected with <see cref="JsonIgnoreAttribute"/> to prevent accidental transport serialization.</description></item>
/// <item><description>Diagnostic <see cref="ToString"/> conceals private identifiers and group names.</description></item>
/// <item><description>Client-facing exposure must be authorized separately via projection DTOs (e.g., GroupEntryDto).</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class GroupEntry
{
    /// <summary>
    /// Gets the unique group identifier.
    /// </summary>
    [JsonIgnore]
    public Guid Id { get; }

    /// <summary>
    /// Gets the validated module and kind scope of the group.
    /// </summary>
    [JsonIgnore]
    public GroupModuleScope Scope { get; }

    /// <summary>
    /// Gets the display name of the group.
    /// </summary>
    [JsonIgnore]
    public string Name { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupEntry"/> class.
    /// </summary>
    /// <param name="id">The unique group identifier. Must not be empty.</param>
    /// <param name="scope">The validated module scope. Must not be null.</param>
    /// <param name="name">The group name. Must not be null or whitespace.</param>
    public GroupEntry(Guid id, GroupModuleScope scope, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Group identifier is required.", nameof(id));
        }

        Scope = scope ?? throw new ArgumentNullException(nameof(scope));

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Group name is required.", nameof(name));
        }

        Id = id;
        Name = name;
    }

    /// <summary>
    /// Returns a non-sensitive diagnostic representation concealing private IDs and group names.
    /// </summary>
    public override string ToString() => "GroupEntry [private]";
}
