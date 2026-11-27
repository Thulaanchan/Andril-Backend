namespace Andril.Common.Accounts.Privacy;

public sealed class PrivacyValidationException : Exception
{
    public PrivacyValidationException() : base("Privacy settings validation failed.") { }
    public PrivacyValidationException(string message) : base(message) { }
}

public sealed class PrivacyNotFoundException : Exception
{
    public PrivacyNotFoundException() : base("Privacy settings not found for specified account.") { }
    public PrivacyNotFoundException(string message) : base(message) { }
}
