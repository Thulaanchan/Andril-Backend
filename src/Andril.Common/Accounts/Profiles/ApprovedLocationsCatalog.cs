using System.Text.Json.Serialization;

namespace Andril.Common.Accounts.Profiles;

public sealed record ApprovedLocationItem(
    [property: JsonPropertyName("city")] string City,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("country")] string Country
);

public static class ApprovedLocationsCatalog
{
    private static readonly IReadOnlyList<ApprovedLocationItem> Locations =
    [
        new("Colombo", "Western Province", "Sri Lanka"),
        new("Kandy", "Central Province", "Sri Lanka"),
        new("Galle", "Southern Province", "Sri Lanka"),
        new("Jaffna", "Northern Province", "Sri Lanka"),
        new("Gampaha", "Western Province", "Sri Lanka"),
        new("Negombo", "Western Province", "Sri Lanka"),
        new("Kurunegala", "North Western Province", "Sri Lanka"),
        new("Anuradhapura", "North Central Province", "Sri Lanka"),
        new("Batticaloa", "Eastern Province", "Sri Lanka"),
        new("Matara", "Southern Province", "Sri Lanka"),
        new("Badulla", "Uva Province", "Sri Lanka"),
        new("Ratnapura", "Sabaragamuwa Province", "Sri Lanka"),
        new("Trincomalee", "Eastern Province", "Sri Lanka"),
        new("Kalutara", "Western Province", "Sri Lanka"),
        new("Nuwara Eliya", "Central Province", "Sri Lanka"),
        new("London", "Greater London", "United Kingdom"),
        new("Toronto", "Ontario", "Canada"),
        new("Melbourne", "Victoria", "Australia"),
        new("Singapore", "Central", "Singapore"),
        new("Dubai", "Dubai", "United Arab Emirates")
    ];

    public static IReadOnlyList<ApprovedLocationItem> GetApprovedLocations() => Locations;

    public static bool IsValidLocation(string? city) =>
        !string.IsNullOrWhiteSpace(city) && Locations.Any(l => string.Equals(l.City, city.Trim(), StringComparison.OrdinalIgnoreCase));
}
