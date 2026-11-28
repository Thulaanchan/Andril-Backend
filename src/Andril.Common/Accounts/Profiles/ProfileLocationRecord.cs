using System.Text.Json.Serialization;

namespace Andril.Common.Accounts.Profiles;

public sealed class ProfileLocationRecord
{
    private ProfileLocationRecord() { }

    public Guid ProfileId { get; private set; }
    public Guid AccountId { get; private set; }
    public string Module { get; private set; } = string.Empty;
    public string City { get; private set; } = string.Empty;
    public string? Region { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static ProfileLocationRecord Create(
        Guid profileId,
        Guid accountId,
        string module,
        string city,
        string? region,
        DateTimeOffset now)
    {
        if (profileId == Guid.Empty) throw new ProfileValidationException();
        if (accountId == Guid.Empty) throw new ProfileValidationException();
        if (string.IsNullOrWhiteSpace(city)) throw new ProfileValidationException();

        var utc = now.ToUniversalTime();
        return new ProfileLocationRecord
        {
            ProfileId = profileId,
            AccountId = accountId,
            Module = module.Trim().ToLowerInvariant(),
            City = city.Trim(),
            Region = region?.Trim(),
            CreatedAtUtc = utc,
            UpdatedAtUtc = utc
        };
    }

    public void Update(string city, string? region, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(city)) throw new ProfileValidationException();
        City = city.Trim();
        Region = region?.Trim();
        UpdatedAtUtc = now.ToUniversalTime();
    }
}

public sealed record ProfileLocationDto(
    [property: JsonPropertyName("profileId")] Guid ProfileId,
    [property: JsonPropertyName("module")] string Module,
    [property: JsonPropertyName("city")] string City,
    [property: JsonPropertyName("region")] string? Region,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc
);

public sealed record UpdateProfileLocationRequest(
    [property: JsonPropertyName("city")] string City,
    [property: JsonPropertyName("region")] string? Region
);
