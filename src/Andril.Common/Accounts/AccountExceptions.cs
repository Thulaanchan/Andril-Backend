namespace Andril.Common.Accounts;

public sealed class AccountAuthenticationRequiredException : Exception
{
    public AccountAuthenticationRequiredException() : base("Authentication is required.") { }
}

public sealed class AccountStoreUnavailableException : Exception
{
    // Intentionally exclude provider exception messages/connection details from this boundary.
    public AccountStoreUnavailableException() : base("The account store is unavailable.") { }
}
