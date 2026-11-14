namespace Andril.API.Authentication;

public sealed class SupabaseAuthOptions
{
    public string ProjectUrl { get; set; } = "";
    public string SigningAlgorithm { get; set; } = "";
    public string Audience { get; set; } = "authenticated";

    // Pin discovery to a hosted Supabase project. Custom domains/self-hosting need
    // a separately reviewed trust configuration; token claims never select a URL.
    public bool TryGetIssuer(out string issuer)
    {
        issuer = "";
        if (SigningAlgorithm is not ("ES256" or "RS256") || Audience != "authenticated" ||
            !Uri.TryCreate(ProjectUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/" ||
            !System.Text.RegularExpressions.Regex.IsMatch(uri.Host,
                "^[a-z0-9-]+\\.supabase\\.co$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return false;

        issuer = uri.GetLeftPart(UriPartial.Authority) + "/auth/v1";
        return true;
    }
}
