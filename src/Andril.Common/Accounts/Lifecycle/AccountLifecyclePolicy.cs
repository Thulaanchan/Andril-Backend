namespace Andril.Common.Accounts.Lifecycle;

// Account-wide product states, distinct from per-module active/inactive membership
// and from operation-specific moderation restrictions. Not mapped to persistence.
internal enum AccountLifecycleState
{
    Active,
    Suspended,
    Deactivated,
    Deleted
}

// Pure transition rules selected by AND-M2-PHASE03A-FINAL-PRODUCT-DECISIONS-20261128.
// This policy supplies neither authentication nor authorization. A future approved
// coordinator must resolve the owner, check current eligibility/security and deletion
// confirmation, and atomically persist state, revision, receipt and audit. No route,
// production registration, administrator permission or retention policy is defined here.
internal static class AccountLifecyclePolicy
{
    internal static bool AllowsOwnerTransition(AccountLifecycleState current, AccountLifecycleState target) =>
        (current, target) switch
        {
            (AccountLifecycleState.Active, AccountLifecycleState.Deactivated) => true,
            (AccountLifecycleState.Deactivated, AccountLifecycleState.Active) => true,
            (AccountLifecycleState.Active or AccountLifecycleState.Deactivated, AccountLifecycleState.Deleted) => true,
            _ => false
        };

    // Necessary account-state condition only. Active never replaces authentication,
    // module eligibility, current standing, privacy or resource authorization checks.
    internal static bool AllowsProtectedAccess(AccountLifecycleState current) =>
        current == AccountLifecycleState.Active;
}
