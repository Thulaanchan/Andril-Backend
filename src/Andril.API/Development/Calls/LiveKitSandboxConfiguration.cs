namespace Andril.API.Development.Calls;

/// <summary>Explicit sandbox opt-in. Never registered in production DI.</summary>
internal static class LiveKitSandboxConfiguration
{
    internal static LiveKitDevelopmentOptions Load(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.EnvironmentName != "ChatRealtimeDevelopment" || configuration["LiveKitSandbox:Enabled"] != "true")
            throw new InvalidOperationException("LiveKit sandbox is disabled.");
        if (!Uri.TryCreate(configuration["LiveKitSandbox:ApiEndpoint"], UriKind.Absolute, out var api) ||
            !Uri.TryCreate(configuration["LiveKitSandbox:RtcEndpoint"], UriKind.Absolute, out var rtc))
            throw new InvalidOperationException("LiveKit sandbox endpoints unavailable.");
        var options = new LiveKitDevelopmentOptions(api, rtc, configuration["LiveKitSandbox:ApiKey"] ?? "",
            configuration["LiveKitSandbox:ApiSecret"] ?? "", TimeSpan.FromMinutes(1));
        // Reuse the provider's endpoint, secret and lifetime validation without making a network call.
        using var client = new HttpClient();
        _ = new LiveKitDevelopmentAdapter(client, options, environment);
        return options;
    }

    internal static HttpClient CreateClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false,
        UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = TimeSpan.FromSeconds(20) };
}
