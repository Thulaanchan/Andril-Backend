namespace Andril.Common.Accounts;

public sealed record AccountDto(Guid AccountId, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
