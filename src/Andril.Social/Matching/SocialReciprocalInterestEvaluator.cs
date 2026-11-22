namespace Andril.Social.Matching;

/// <summary>
/// Status of reciprocal interest evaluation between two Social participants based on supplied evidence.
/// </summary>
public enum SocialReciprocalInterestStatus
{
    /// <summary>
    /// Both participants have unambiguous recorded interest in each other.
    /// </summary>
    MutualInterestObserved,

    /// <summary>
    /// At least one participant has an explicit negative interest recorded (e.g. pass, unmatch, or decline).
    /// </summary>
    NotMutual,

    /// <summary>
    /// Interest evidence is missing, unknown, or incomplete for at least one participant while neither has explicitly passed.
    /// </summary>
    Indeterminate
}

/// <summary>
/// Pure, deterministic domain evaluator for interpreting reciprocal interest signals between Social candidates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and Architectural Boundaries:</b><br />
/// This utility provides an offline interpretation of supplied Boolean interest evidence only.
/// It operates purely in memory on two nullable Boolean values representing directional interest.
/// </para>
/// <para>
/// <b>Important Security and Functional Constraints:</b><br />
/// <list type="bullet">
/// <item><description>It does NOT establish or persist a mutual match entity in a database.</description></item>
/// <item><description>It does NOT authorize chat conversations or fulfill M4's <c>ChatConversationAccessEvidence</c> contract.</description></item>
/// <item><description>It does NOT check user authentication, Social profile ownership, or Dating module opt-in.</description></item>
/// <item><description>It does NOT check bidirectional blocking, user reports, or moderation account standing.</description></item>
/// <item><description>It does NOT send push notifications, dispatch domain events, or mutate state.</description></item>
/// <item><description>It does NOT calculate personality compatibility scores or ranking weights.</description></item>
/// </list>
/// </para>
/// </remarks>
public static class SocialReciprocalInterestEvaluator
{
    /// <summary>
    /// Evaluates reciprocal interest between two participants based on supplied directional interest evidence.
    /// </summary>
    /// <param name="firstInterestedInSecond">
    /// Directional interest recorded by the first participant toward the second participant:
    /// <see langword="true"/> if interested (e.g. like), <see langword="false"/> if not interested (e.g. pass),
    /// or <see langword="null"/> if unrecorded/unknown.
    /// </param>
    /// <param name="secondInterestedInFirst">
    /// Directional interest recorded by the second participant toward the first participant:
    /// <see langword="true"/> if interested (e.g. like), <see langword="false"/> if not interested (e.g. pass),
    /// or <see langword="null"/> if unrecorded/unknown.
    /// </param>
    /// <returns>
    /// <see cref="SocialReciprocalInterestStatus.MutualInterestObserved"/> if both values are <see langword="true"/>;
    /// <see cref="SocialReciprocalInterestStatus.NotMutual"/> if either value is <see langword="false"/>;
    /// otherwise <see cref="SocialReciprocalInterestStatus.Indeterminate"/>.
    /// </returns>
    public static SocialReciprocalInterestStatus Evaluate(
        bool? firstInterestedInSecond,
        bool? secondInterestedInFirst)
    {
        if (firstInterestedInSecond == true && secondInterestedInFirst == true)
        {
            return SocialReciprocalInterestStatus.MutualInterestObserved;
        }

        if (firstInterestedInSecond == false || secondInterestedInFirst == false)
        {
            return SocialReciprocalInterestStatus.NotMutual;
        }

        return SocialReciprocalInterestStatus.Indeterminate;
    }
}
