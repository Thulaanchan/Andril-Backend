namespace Andril.Shared.Authentication;

/// <summary>The verified authentication identity, not an account or module profile.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    bool IsAuthenticated { get; }
}
