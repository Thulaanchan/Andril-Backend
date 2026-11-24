using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Andril.Social.Personality;

/// <summary>
/// Interaction tendency along the Social Engagement axis.
/// Derived deterministically from questions q01, q07, q09 (mean &lt; 3: Reserved; mean &gt; 3: Outgoing).
/// </summary>
public enum SocialEngagementTendency
{
    Reserved,
    Outgoing
}

/// <summary>
/// Interaction tendency along the Communication axis.
/// Derived deterministically from questions q03, q05, q06 (mean &lt; 3: LessExpressive; mean &gt; 3: Expressive).
/// </summary>
public enum CommunicationTendency
{
    LessExpressive,
    Expressive
}

/// <summary>
/// Interaction tendency along the Planning axis.
/// Derived deterministically from question q10 (score &lt; 3: Spontaneous; score &gt; 3: Structured).
/// </summary>
public enum PlanningTendency
{
    Spontaneous,
    Structured
}

/// <summary>
/// Canonical identifiers for the eight approved descriptive interaction archetypes under SOC-01 / SOC-02.
/// </summary>
public enum PersonalityArchetypeId
{
    AP01,
    AP02,
    AP03,
    AP04,
    AP05,
    AP06,
    AP07,
    AP08
}

/// <summary>
/// Immutable descriptive interaction profile archetype.
/// Designed as a lightweight interaction preference profile, not a validated psychological diagnostic.
/// </summary>
public sealed class PersonalityArchetype : IEquatable<PersonalityArchetype>
{
    public PersonalityArchetypeId Id { get; }
    public string Name { get; }
    public SocialEngagementTendency Social { get; }
    public CommunicationTendency Communication { get; }
    public PlanningTendency Planning { get; }

    private PersonalityArchetype(
        PersonalityArchetypeId id,
        string name,
        SocialEngagementTendency social,
        CommunicationTendency communication,
        PlanningTendency planning)
    {
        Id = id;
        Name = name;
        Social = social;
        Communication = communication;
        Planning = planning;
    }

    public static readonly PersonalityArchetype AP01 = new(
        PersonalityArchetypeId.AP01, "The Quiet Planner", SocialEngagementTendency.Reserved, CommunicationTendency.LessExpressive, PlanningTendency.Structured);

    public static readonly PersonalityArchetype AP02 = new(
        PersonalityArchetypeId.AP02, "The Quiet Explorer", SocialEngagementTendency.Reserved, CommunicationTendency.LessExpressive, PlanningTendency.Spontaneous);

    public static readonly PersonalityArchetype AP03 = new(
        PersonalityArchetypeId.AP03, "The Thoughtful Communicator", SocialEngagementTendency.Reserved, CommunicationTendency.Expressive, PlanningTendency.Structured);

    public static readonly PersonalityArchetype AP04 = new(
        PersonalityArchetypeId.AP04, "The Gentle Adventurer", SocialEngagementTendency.Reserved, CommunicationTendency.Expressive, PlanningTendency.Spontaneous);

    public static readonly PersonalityArchetype AP05 = new(
        PersonalityArchetypeId.AP05, "The Social Organizer", SocialEngagementTendency.Outgoing, CommunicationTendency.LessExpressive, PlanningTendency.Structured);

    public static readonly PersonalityArchetype AP06 = new(
        PersonalityArchetypeId.AP06, "The Free Spirit", SocialEngagementTendency.Outgoing, CommunicationTendency.LessExpressive, PlanningTendency.Spontaneous);

    public static readonly PersonalityArchetype AP07 = new(
        PersonalityArchetypeId.AP07, "The Social Connector", SocialEngagementTendency.Outgoing, CommunicationTendency.Expressive, PlanningTendency.Structured);

    public static readonly PersonalityArchetype AP08 = new(
        PersonalityArchetypeId.AP08, "The Enthusiast", SocialEngagementTendency.Outgoing, CommunicationTendency.Expressive, PlanningTendency.Spontaneous);

    public static readonly IReadOnlyList<PersonalityArchetype> All = new ReadOnlyCollection<PersonalityArchetype>(
        [AP01, AP02, AP03, AP04, AP05, AP06, AP07, AP08]);

    public static PersonalityArchetype FromAxes(
        SocialEngagementTendency social,
        CommunicationTendency communication,
        PlanningTendency planning)
    {
        return (social, communication, planning) switch
        {
            (SocialEngagementTendency.Reserved, CommunicationTendency.LessExpressive, PlanningTendency.Structured) => AP01,
            (SocialEngagementTendency.Reserved, CommunicationTendency.LessExpressive, PlanningTendency.Spontaneous) => AP02,
            (SocialEngagementTendency.Reserved, CommunicationTendency.Expressive, PlanningTendency.Structured) => AP03,
            (SocialEngagementTendency.Reserved, CommunicationTendency.Expressive, PlanningTendency.Spontaneous) => AP04,
            (SocialEngagementTendency.Outgoing, CommunicationTendency.LessExpressive, PlanningTendency.Structured) => AP05,
            (SocialEngagementTendency.Outgoing, CommunicationTendency.LessExpressive, PlanningTendency.Spontaneous) => AP06,
            (SocialEngagementTendency.Outgoing, CommunicationTendency.Expressive, PlanningTendency.Structured) => AP07,
            (SocialEngagementTendency.Outgoing, CommunicationTendency.Expressive, PlanningTendency.Spontaneous) => AP08,
            _ => throw new InvalidOperationException("Invalid tendency combination.")
        };
    }

    public bool Equals(PersonalityArchetype? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id == other.Id;
    }

    public override bool Equals(object? obj) => obj is PersonalityArchetype other && Equals(other);
    public override int GetHashCode() => (int)Id;
    public override string ToString() => $"{Id}: {Name}";
}

/// <summary>
/// Voluntary user confirmation evidence for unresolved neutral axes (mean == 3).
/// Must be bound to the exact questionnaire revision.
/// </summary>
public sealed record PersonalityArchetypeConfirmation(
    string QuestionnaireRevision,
    SocialEngagementTendency? SocialConfirmation = null,
    CommunicationTendency? CommunicationConfirmation = null,
    PlanningTendency? PlanningConfirmation = null
);

/// <summary>
/// Deterministic result of personality archetype classification.
/// </summary>
public sealed class PersonalityArchetypeClassificationResult
{
    [JsonIgnore]
    public bool IsSuccess => Archetype is not null;

    [JsonIgnore]
    public PersonalityArchetype? Archetype { get; }

    [JsonIgnore]
    public SocialEngagementTendency? SocialTendency { get; }

    [JsonIgnore]
    public CommunicationTendency? CommunicationTendency { get; }

    [JsonIgnore]
    public PlanningTendency? PlanningTendency { get; }

    [JsonIgnore]
    public bool HasUnresolvedAxes => UnresolvedAxes.Count > 0;

    [JsonIgnore]
    public IReadOnlyList<string> UnresolvedAxes { get; }

    [JsonIgnore]
    public string? ErrorMessage { get; }

    private PersonalityArchetypeClassificationResult(
        PersonalityArchetype? archetype,
        SocialEngagementTendency? social,
        CommunicationTendency? communication,
        PlanningTendency? planning,
        IReadOnlyList<string> unresolvedAxes,
        string? errorMessage)
    {
        Archetype = archetype;
        SocialTendency = social;
        CommunicationTendency = communication;
        PlanningTendency = planning;
        UnresolvedAxes = unresolvedAxes ?? Array.Empty<string>();
        ErrorMessage = errorMessage;
    }

    public static PersonalityArchetypeClassificationResult Success(
        PersonalityArchetype archetype,
        SocialEngagementTendency social,
        CommunicationTendency communication,
        PlanningTendency planning) =>
        new(archetype, social, communication, planning, Array.Empty<string>(), null);

    public static PersonalityArchetypeClassificationResult Unresolved(
        SocialEngagementTendency? social,
        CommunicationTendency? communication,
        PlanningTendency? planning,
        IReadOnlyList<string> unresolvedAxes) =>
        new(null, social, communication, planning, unresolvedAxes, "Archetype classification unresolved pending voluntary confirmation.");

    public static PersonalityArchetypeClassificationResult Failed(string errorMessage) =>
        new(null, null, null, null, Array.Empty<string>(), errorMessage);

    public override string ToString() =>
        IsSuccess ? $"Classification: {Archetype!.Id} ({Archetype.Name})" : $"Classification Incomplete: {ErrorMessage}";
}
