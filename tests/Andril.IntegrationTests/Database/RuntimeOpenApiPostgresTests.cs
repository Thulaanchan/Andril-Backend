using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed partial class SocialMatchingHttpPostgresTests
{
    [LocalPostgresFact]
    public async Task PublishedM5OpenApiMatchesTheEnabledRuntimeAndKeepsUnavailableOperationsUnmapped()
    {
        await using var database = await OpenAsync();
        await using var groups = await SharedGroupsDatabase.CreateAsync(sharedDatabase: database.Database);
        await GrantCombinedMatchingAsync(database);
        using var host = new Host(database, questionnaire: true, interestSelections: true, personality: true, groupRole: groups.Role);
        using var client = Client(host);
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var methods = new HashSet<string>(StringComparer.Ordinal) { "get", "post", "put", "patch", "delete" };
        var contracts = new[] { "social-questionnaire.v1.yaml", "social-matching.v1.yaml", "shared-groups.v1.yaml" };
        var published = contracts.SelectMany(ReadPublishedOperations).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(37, published.Length);

        var paths = document.GetProperty("paths").EnumerateObject().ToArray();
        var enabled = paths.Where(path => path.Name.StartsWith("/api/v1/social/", StringComparison.Ordinal)
                    || path.Name == "/api/v1/groups" || path.Name.StartsWith("/api/v1/groups/", StringComparison.Ordinal))
            .SelectMany(path => path.Value.EnumerateObject().Where(method => methods.Contains(method.Name))
                .Select(method => $"{method.Name} {path.Name.TrimEnd('/')}"))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(published, enabled);
        Assert.DoesNotContain(paths, path => path.Name.StartsWith("/api/v1/account/modules", StringComparison.Ordinal));

        IEnumerable<string> ReadPublishedOperations(string file)
        {
            // The versioned contracts use canonical two-space path and four-space operation keys.
            // Ignore components and schema fields outside the top-level paths map.
            string? path = null;
            foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, file)).SkipWhile(line => line != "paths:").Skip(1))
            {
                if (line.Length == 0) continue;
                if (line[0] != ' ') yield break;
                if (line.StartsWith("  /", StringComparison.Ordinal) && line.EndsWith(':'))
                    path = line[2..^1].TrimEnd('/');
                else if (path is not null && line.StartsWith("    ", StringComparison.Ordinal) && line.EndsWith(':')
                    && methods.Contains(line[4..^1]))
                    yield return $"{line[4..^1]} {path}";
            }
        }
    }
}
