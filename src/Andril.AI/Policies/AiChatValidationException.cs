
namespace Andril.AI.Policies;

/// <summary>
/// Represents an invalid AI Chat request.
///
/// At the HTTP API boundary, map this exception
/// to a safe 400 Bad Request response.
/// </summary>
public sealed class AiChatValidationException(
    string message) : Exception(message);
