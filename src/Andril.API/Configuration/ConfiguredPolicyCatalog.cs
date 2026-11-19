using Andril.Common.Accounts.Policies;

namespace Andril.API.Configuration;

// A scoped provider captures one catalog per request. Published version identifiers must be immutable.
public sealed class ConfiguredPolicyCatalog(IConfiguration configuration) : IPolicyCatalogProvider
{
    private PolicyCatalog? current;
    public PolicyCatalog GetCurrent() => current ??= new(Read("Terms"), Read("Privacy"), Read("CommunityGuidelines"));

    private PolicyDocument Read(string name)
    {
        var section = configuration.GetSection("AccountPolicies:" + name);
        var version = section["Version"];
        var url = section["Url"];
        var language = section["Language"];
        if (!AccountPolicyRecord.ValidVersion(version) || language != "en"
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0)
            throw new PolicyConfigurationException();
        return new(version!, url!, language);
    }
}
