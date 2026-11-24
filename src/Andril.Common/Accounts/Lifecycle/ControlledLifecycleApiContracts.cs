using System.Data.Common;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Policies;
using Andril.Shared.Authentication;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Common.Accounts.Lifecycle;

internal sealed record LifecycleApiResult<T>(HttpStatusCode Status, T? Body = null) where T : class
{
    internal string CacheControl => "no-store";
}

internal sealed record DeactivateModuleRequest(
    [property: JsonPropertyName("expectedRevision")] long ExpectedRevision)
{
    // Parse explicitly because ordinary JSON binding can accept repeated properties.
    internal static DeactivateModuleRequest? Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return null;
        var properties = body.EnumerateObject();
        if (!properties.MoveNext())
            return null;
        var property = properties.Current;
        if (!property.NameEquals("expectedRevision") || property.Value.ValueKind != JsonValueKind.Number ||
            !property.Value.TryGetInt64(out var revision) || revision is <= 0 or long.MaxValue ||
            properties.MoveNext())
            return null;
        return new(revision);
    }
}

internal sealed record LifecycleModuleStatus(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("revision"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Revision = null);

internal sealed record LifecycleModuleStatuses(
    [property: JsonPropertyName("learning")] LifecycleModuleStatus Learning,
    [property: JsonPropertyName("hobbies")] LifecycleModuleStatus Hobbies,
    [property: JsonPropertyName("social")] LifecycleModuleStatus Social);

// Isolated handlers for contract testing. HTTP mappings and the aggregate status projection
// are provisional. There is no route registration or new authority, store, or journal here.
internal sealed class ControlledLifecycleApiContracts(ICurrentUser user, IOnboardingIdentity identity,
    IAccountRepository accounts, OfflineModuleLifecycleService transitions, ModuleNavigationService navigation)
{
    internal async Task<LifecycleApiResult<ModuleLifecycleResult>> DeactivateAsync(string module,
        IReadOnlyList<string> idempotencyKeys, JsonElement body, Guid correlationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (GetSubject() is not Guid subject)
            return new(HttpStatusCode.Unauthorized);
        if (!identity.IsConfirmedFor(subject))
            return new(HttpStatusCode.Forbidden);
        if (!ModuleNames.IsValid(module) || idempotencyKeys.Count != 1 ||
            !Guid.TryParseExact(idempotencyKeys[0], "D", out var key) || key == Guid.Empty ||
            DeactivateModuleRequest.Parse(body) is not { } request)
            return new(HttpStatusCode.BadRequest);

        try
        {
            var account = await accounts.FindBySubjectAsync(subject, ct);
            ct.ThrowIfCancellationRequested();
            if (account is null || account.Id == Guid.Empty || account.SupabaseUserId != subject ||
                GetSubject() != subject || !identity.IsConfirmedFor(subject))
                return new(HttpStatusCode.Forbidden);

            var result = await transitions.ExecuteAsync(subject,
                new(module, key, false, request.ExpectedRevision, ModuleLifecycleOperation.Deactivate,
                    new(account.Id, module)), correlationId, ct);
            return GetSubject() == subject && identity.IsConfirmedFor(subject)
                ? new(HttpStatusCode.OK, result) : new(HttpStatusCode.Forbidden);
        }
        catch (ModuleLifecycleException error)
        {
            return new(error.Denial switch
            {
                ModuleLifecycleDenial.ConflictingTransition or ModuleLifecycleDenial.AlreadyInactive => HttpStatusCode.Conflict,
                ModuleLifecycleDenial.MembershipMissing => HttpStatusCode.NotFound,
                ModuleLifecycleDenial.StaleEvidence or ModuleLifecycleDenial.EvidenceUnavailable => HttpStatusCode.ServiceUnavailable,
                _ => HttpStatusCode.Forbidden
            });
        }
        catch (Exception error) when (error is AccountStoreUnavailableException or PolicyConfigurationException
            or ModuleLifecycleCommitUnknownException or DbException or TimeoutException or IOException)
        {
            return new(HttpStatusCode.ServiceUnavailable);
        }
    }

    internal async Task<LifecycleApiResult<LifecycleModuleStatuses>> ReadStatusesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (GetSubject() is not Guid subject)
            return new(HttpStatusCode.Unauthorized);
        if (!identity.IsConfirmedFor(subject))
            return new(HttpStatusCode.Forbidden);

        // Each status is a separate read. This result is navigation data, not an atomic
        // three-module snapshot or evidence for a protected operation.
        var statuses = new List<LifecycleModuleStatus>(3);
        foreach (var module in new[] { ModuleNames.Learning, ModuleNames.Hobbies, ModuleNames.Social })
        {
            var status = await navigation.ReadAsync(module, ct);
            ct.ThrowIfCancellationRequested();
            if (GetSubject() != subject || !identity.IsConfirmedFor(subject))
                return new(HttpStatusCode.Forbidden);
            if (status.State == ModuleNavigationState.Unavailable)
                return new(HttpStatusCode.ServiceUnavailable);

            statuses.Add(status.State switch
            {
                ModuleNavigationState.Active => new("active", status.Revision),
                ModuleNavigationState.Inactive => new("inactive", status.Revision),
                ModuleNavigationState.NeverActivated => new("neverActivated"),
                _ => new("denied")
            });
        }
        return new(HttpStatusCode.OK, new(statuses[0], statuses[1], statuses[2]));
    }

    private Guid? GetSubject() => user.IsAuthenticated && user.UserId is Guid subject && subject != Guid.Empty
        ? subject : null;
}
