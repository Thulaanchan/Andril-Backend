using System.Text.Json.Serialization;

namespace Andril.Learning.Portfolio;

/// <summary>
/// LRN-06 / DEC-20261120-LRN06-01: private, manually edited project content only.
/// This is not an authorized public payload. Ownership, publication, rendering, storage,
/// detailed field schemas and deletion policies belong to future approved integration.
/// Media and links are inert references: no fetching, file access or cross-module lookup occurs.
/// </summary>
public sealed class PortfolioProject
{
    public Guid Id { get; }
    [JsonIgnore]
    public string? Title { get; }
    [JsonIgnore]
    public string? Description { get; }
    [JsonIgnore]
    public IReadOnlyList<string> MediaReferences { get; }
    [JsonIgnore]
    public IReadOnlyList<string> ExternalLinks { get; }

    /// <summary>Incomplete drafts are allowed. Text is preserved without invented limits.
    /// Optional reference collections default to empty; supplied references must be absolute
    /// HTTP(S) URLs. This validates syntax and scheme only, not resource safety or accessibility.</summary>
    public PortfolioProject(Guid id, string? title = null, string? description = null,
        IEnumerable<string>? mediaReferences = null, IEnumerable<string>? externalLinks = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("A Portfolio project ID is required.", nameof(id));
        var media = CopyReferences(mediaReferences, nameof(mediaReferences));
        var links = CopyReferences(externalLinks, nameof(externalLinks));
        Id = id;
        Title = title;
        Description = description;
        MediaReferences = media;
        ExternalLinks = links;
    }

    /// <summary>Explicitly replace editable content while retaining stable project identity.
    /// The original and its snapshots remain unchanged, including when validation or enumeration fails.
    /// The caller must adopt the returned replacement after its own authorization checks.</summary>
    public PortfolioProject WithContent(string? title, string? description,
        IEnumerable<string>? mediaReferences = null, IEnumerable<string>? externalLinks = null) =>
        new(Id, title, description, mediaReferences, externalLinks);

    private static IReadOnlyList<string> CopyReferences(IEnumerable<string>? references, string parameterName)
    {
        var copy = references?.ToArray() ?? [];
        foreach (var reference in copy)
        {
            if (string.IsNullOrWhiteSpace(reference) ||
                !Uri.TryCreate(reference, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrEmpty(uri.Host))
                throw new ArgumentException("References must be absolute HTTP or HTTPS URLs.", parameterName);
        }
        return Array.AsReadOnly(copy);
    }

    public override string ToString() => "Private Portfolio project [identity and content omitted]";
}
