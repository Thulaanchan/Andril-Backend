using System.Security.Claims;
using System.Text.Encodings.Web;
using Andril.API.Authentication;
using Andril.Communication.Interfaces;
using Andril.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Andril.IntegrationTests.Communication.Chat.Prototype;

/// <summary>
/// Standalone test assembly host. No Program, configuration sources, controllers, routes,
/// sockets, real identity, persistence or delivery services. The probe is an in-process
/// HttpContext feature, impossible to select through HTTP; every HTTP response is empty 404.
/// </summary>
internal sealed class ChatPrototypeTestHost : IAsyncDisposable
{
    internal static readonly Guid Conversation = SyntheticIdentity.Conversation;
    private readonly IHost host;
    private bool disposed;
    internal ChatSyntheticAuthorizationFixture Evidence { get; }
    internal SyntheticIdentity? Identity { get; }
    internal HttpClient Client { get; }
    internal IReadOnlyList<ServiceDescriptor> Registrations { get; }
    internal TestServer Server => host.GetTestServer();
    internal IServiceProvider Services => host.Services;

    private ChatPrototypeTestHost(IHost host, ChatSyntheticAuthorizationFixture fixture, SyntheticIdentity? identity, IReadOnlyList<ServiceDescriptor> registrations)
    {
        Registrations = registrations;
        this.host = host;
        Evidence = fixture;
        Identity = identity;
        Client = host.GetTestClient();
    }

    internal static async Task<ChatPrototypeTestHost> StartAsync(SyntheticActor actor,
        Action<IServiceCollection>? configureTestServices = null)
    {
        var identity = SyntheticIdentity.Select(actor);
        var fixture = ChatSyntheticAuthorizationFixture.ForIdentity(identity);
        IHost? built = null;
        ServiceDescriptor[] registrations = [];
        try
        {
            // Deliberately not CreateDefaultBuilder: no environment/appsettings production configuration.
            built = new HostBuilder().ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
                {
                    services.AddSingleton(new FixedPrincipal(identity));
                    services.AddAuthentication("SyntheticChatOnly")
                        .AddScheme<AuthenticationSchemeOptions, FixedAuthenticationHandler>("SyntheticChatOnly", _ => { });
                    services.AddHttpContextAccessor();
                    services.AddScoped<ICurrentUser, HttpCurrentUser>();
                    services.AddScoped<IChatAccessAuthorizer>(provider => fixture.CreatePolicy(provider.GetRequiredService<ICurrentUser>()));
                    services.AddScoped<RequestActorState>();
                    configureTestServices?.Invoke(services);
                    registrations = services.ToArray();
                }).Configure(app =>
                {
                    app.UseAuthentication();
                    app.Run(async context =>
                    {
                        var probe = context.Features.Get<InMemoryProbe>();
                        if (probe is not null) await probe.Run(context);
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                    });
                })).Build();
            await built.StartAsync();
            return new ChatPrototypeTestHost(built, fixture, identity, registrations);
        }
        catch
        {
            built?.Dispose();
            fixture.Dispose();
            // Do not propagate private configuration/dependency details.
            throw new InvalidOperationException("Synthetic Chat host setup failed.");
        }
    }

    internal async Task<ProbeResult> ProbeAsync(ChatModule module, Action<HttpContext>? prepareRequest = null, Guid? conversation = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ProbeResult? result = null;
        await Server.SendAsync(context =>
        {
            context.Request.Path = "/unmapped";
            prepareRequest?.Invoke(context);
            context.Features.Set(new InMemoryProbe(async request =>
            {
                var user = request.RequestServices.GetRequiredService<ICurrentUser>();
                var state = request.RequestServices.GetRequiredService<RequestActorState>();
                state.Actor = await request.RequestServices.GetRequiredService<IChatAccessAuthorizer>()
                    .AuthorizeConversationAsync(module, conversation ?? Conversation, request.RequestAborted);
                result = new ProbeResult(user.IsAuthenticated, user.UserId, state.Actor, state);
            }));
        });
        return result ?? throw new InvalidOperationException("Synthetic probe did not execute.");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        Client.Dispose();
        try { await host.StopAsync(); }
        finally
        {
            host.Dispose();
            Evidence.Dispose();
        }
    }

    public override string ToString() => nameof(ChatPrototypeTestHost);

    private sealed record FixedPrincipal(SyntheticIdentity? Identity)
    {
        public override string ToString() => nameof(FixedPrincipal);
    }

    // Public constructor is needed only for the authentication handler's DI activation.
    private sealed class FixedAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, FixedPrincipal principal)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (principal.Identity is not { } identity)
                return Task.FromResult(AuthenticateResult.NoResult());
            // No bearer/JWT parsing, token issuance, headers, body, account/profile claims or external lookups.
            var claims = new[] { new Claim("sub", identity.Subject.ToString()),
                new Claim("role", "authenticated"), new Claim("is_anonymous", "false") };
            var user = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, Scheme.Name)));
        }
    }

    private sealed record InMemoryProbe(Func<HttpContext, Task> Run);

    internal sealed class RequestActorState : IDisposable
    {
        internal ChatAuthorizedActor? Actor { get; set; }
        internal bool IsDisposed { get; private set; }
        public void Dispose() { Actor = null; IsDisposed = true; }
        public override string ToString() => nameof(RequestActorState);
    }

    internal sealed record ProbeResult(bool IsAuthenticated, Guid? Subject, ChatAuthorizedActor? Actor, RequestActorState State)
    {
        public override string ToString() => nameof(ProbeResult);
    }
}
