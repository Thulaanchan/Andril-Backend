
using Andril.AI.ChatBox;

namespace Andril.AI.DTOs;

/// <summary>
/// Internal service result; not a public HTTP API contract.
/// </summary>
public sealed record AiChatReply(
    AiModule Module,
    string Text,
    bool IsMock);
