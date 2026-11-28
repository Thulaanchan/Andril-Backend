using System.Text.Json.Serialization;
using Andril.Learning.Activities;

namespace Andril.Learning.Discovery;

public enum LearningPublicationKind { People=1, MentorshipOffer=2 }
public enum LearningPublicationOutcome { Available, Saved, Denied, Invalid, Conflict, NotFound, Unavailable, CommitUnknown }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LearningDisclosureChange([property:JsonRequired]bool Visible,[property:JsonRequired]bool DisplayNameDisclosure,LearningActivityRevision? Expected=null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LearningOfferPublicationChange([property:JsonRequired]bool Published,LearningActivityRevision? Expected=null);
public sealed record LearningPublicationView(LearningPublicationKind Kind,Guid ResourceId,bool Published,bool DisplayNameDisclosure,LearningActivityRevision? Revision);
public sealed record LearningPublicationResult(LearningPublicationOutcome Outcome,LearningPublicationView? Publication=null)
{
    public override string ToString()=>$"Learning publication outcome: {Outcome}";
}
/// <summary>Owner identity and source content are resolved server-side. Consent never grants AI or other-module access.</summary>
public interface ILearningPublicationRepository
{
    Task<LearningPublicationResult> ReadAsync(LearningPublicationKind kind,Guid? offerId=null,CancellationToken cancellationToken=default);
    Task<LearningPublicationResult> SetDisclosureAsync(LearningDisclosureChange change,CancellationToken cancellationToken=default);
    Task<LearningPublicationResult> SetOfferAsync(Guid offerId,LearningOfferPublicationChange change,CancellationToken cancellationToken=default);
}
