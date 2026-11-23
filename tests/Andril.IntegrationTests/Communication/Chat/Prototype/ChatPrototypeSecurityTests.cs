using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using Andril.API.Extensions;
using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Andril.IntegrationTests.Communication.Chat.Prototype;

public sealed class ChatPrototypeSecurityTests
{
    [Fact]
    public async Task Host_uses_only_in_memory_server_with_exact_test_routes_and_no_external_services()
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Learning);
        Assert.IsType<TestServer>(host.Services.GetRequiredService<IServer>());
        var addresses = host.Server.Features.Get<IServerAddressesFeature>();
        Assert.True(addresses is null || addresses.Addresses.Count == 0);
        var routes = host.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).Cast<RouteEndpoint>().ToArray();
        Assert.Equal(4, routes.Length);
        Assert.All(routes, route => Assert.StartsWith("/__test/chat/v1/", route.RoutePattern.RawText));
        var schemes = await host.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
        Assert.Equal("SyntheticChatOnly", Assert.Single(schemes).Name);
        Assert.Null(host.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection"));
        Assert.Null(host.Services.GetService<IHttpClientFactory>());
        var types = host.Registrations.SelectMany(descriptor => new[] { descriptor.ServiceType, descriptor.ImplementationType });
        Assert.DoesNotContain(types.Where(type => type is not null), type =>
            type!.FullName!.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
            || type.FullName.Contains("Supabase", StringComparison.OrdinalIgnoreCase)
            || type.FullName.Contains("DbContext", StringComparison.Ordinal)
            || type.FullName.StartsWith("Andril.Accounts.", StringComparison.Ordinal)
            || type.FullName.StartsWith("Andril.Communication.Notifications.", StringComparison.Ordinal));
        Assert.True(typeof(ChatPrototypeTestHost).IsNotPublic);
        Assert.True(typeof(ChatSyntheticAuthorizationFixture).IsNotPublic);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/admin")]
    [InlineData("/debug")]
    [InlineData("/actor")]
    [InlineData("/api/v1/chat/conversations")]
    [InlineData("/api/v1/chat/messages")]
    [InlineData("/api/v1/onboarding")]
    public async Task No_operation_or_identity_selection_endpoint_exists(string path)
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        using var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.False(response.Headers.Contains("Authorization"));
        Assert.Equal(0, host.Evidence.Reads);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Fixed_synthetic_identity_uses_real_policy_and_cannot_substitute_other_modules(int value)
    {
        var actor = (SyntheticActor)value;
        await using var host = await ChatPrototypeTestHost.StartAsync(actor);
        var identity = Assert.IsType<SyntheticIdentity>(host.Identity);
        var result = await host.ProbeAsync(identity.Module);
        Assert.True(result.IsAuthenticated);
        var authorized = Assert.IsType<ChatAuthorizedActor>(result.Actor);
        Assert.Equal(identity.Subject, authorized.VerifiedSubjectId);
        Assert.Equal(identity.Account, authorized.AccountId);
        Assert.Equal(identity.Profile, authorized.ModuleProfileId);
        Assert.Equal(identity.Module, authorized.Module);
        Assert.Equal("ChatConversationAccessPolicy", host.Evidence.Policy.GetType().Name);
        foreach (var other in Enum.GetValues<ChatModule>().Where(module => module != identity.Module))
            Assert.Null((await host.ProbeAsync(other)).Actor);
    }

    [Fact]
    public async Task An_authenticated_principal_does_not_create_evidence_for_an_unknown_conversation()
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        var result = await host.ProbeAsync(ChatModule.Social, conversation: Guid.Parse("10000000-0000-4000-8000-000000000099"));
        Assert.True(result.IsAuthenticated);
        Assert.Null(result.Actor);
        Assert.Equal(0, host.Evidence.Reads);
    }

    [Fact]
    public void Modules_have_independent_subject_account_and_private_profile_identities()
    {
        var identities = new[] { SyntheticActor.Learning, SyntheticActor.Hobbies, SyntheticActor.Social }
            .Select(actor => SyntheticIdentity.Select(actor)!).ToArray();
        Assert.Equal(3, identities.Select(identity => identity.Subject).Distinct().Count());
        Assert.Equal(3, identities.Select(identity => identity.Account).Distinct().Count());
        Assert.Equal(3, identities.Select(identity => identity.Profile).Distinct().Count());
        Assert.Equal(Enum.GetValues<ChatModule>(), identities.Select(identity => identity.Module));
    }

    [Fact]
    public async Task Headers_body_query_and_fake_bearer_cannot_select_account_or_profile()
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Learning);
        var other = SyntheticIdentity.Select(SyntheticActor.Social)!;
        using var body = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"accountId\":\"{other.Account}\",\"moduleProfileId\":\"{other.Profile}\",\"sub\":\"{other.Subject}\"}}"));
        var result = await host.ProbeAsync(ChatModule.Learning, context =>
        {
            context.Request.Method = "POST";
            context.Request.Headers.Authorization = "Bearer synthetic-not-a-jwt";
            context.Request.Headers["X-Account-Id"] = other.Account.ToString();
            context.Request.Headers["X-Profile-Id"] = other.Profile.ToString();
            context.Request.Headers["X-Subject-Id"] = other.Subject.ToString();
            context.Request.QueryString = new QueryString($"?actor=Social&accountId={other.Account}");
            context.Request.Body = body;
            context.Request.ContentType = "application/json";
        });
        Assert.Equal(host.Identity!.Subject, result.Subject);
        Assert.Equal(host.Identity.Account, result.Actor!.AccountId);
        Assert.Equal(host.Identity.Profile, result.Actor.ModuleProfileId);
        Assert.NotEqual(other.Account, result.Actor.AccountId);
        Assert.NotEqual(other.Profile, result.Actor.ModuleProfileId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public async Task Anonymous_or_unknown_identity_cannot_be_promoted_by_client_input(int actor)
    {
        await using var host = await ChatPrototypeTestHost.StartAsync((SyntheticActor)actor);
        var result = await host.ProbeAsync(ChatModule.Social, context =>
        {
            context.Request.Headers.Authorization = "Bearer invented";
            context.Request.Headers["X-Actor"] = "Social";
        });
        Assert.False(result.IsAuthenticated);
        Assert.Null(result.Subject);
        Assert.Null(result.Actor);
        Assert.Equal(0, host.Evidence.Reads);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("conflicting")]
    public async Task Authentication_alone_never_grants_access_with_unavailable_or_invalid_evidence(string scenario)
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        if (scenario == "missing") host.Evidence.RemoveEvidence();
        if (scenario == "stale") host.Evidence.StaleEvidence();
        if (scenario == "conflicting") host.Evidence.ConflictingEvidence();
        var result = await host.ProbeAsync(ChatModule.Social);
        Assert.True(result.IsAuthenticated);
        Assert.Null(result.Actor);
        Assert.Equal(1, host.Evidence.Reads);
    }

    [Theory]
    [InlineData("IsAdult", false)]
    [InlineData("IsAdult", null)]
    [InlineData("HasSocialOptIn", false)]
    [InlineData("HasSocialOptIn", null)]
    [InlineData("HasMutualMatch", false)]
    [InlineData("HasMutualMatch", null)]
    [InlineData("IsBlocked", true)]
    [InlineData("IsBlocked", null)]
    public async Task Social_adult_opt_in_match_and_block_evidence_is_required(string flag, bool? value)
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        host.Evidence.SetFlag(flag, value);
        Assert.Null((await host.ProbeAsync(ChatModule.Social)).Actor);
        Assert.Equal(1, host.Evidence.Reads);
    }

    [Theory]
    [InlineData("block")]
    [InlineData("unmatch")]
    [InlineData("eligibility")]
    public async Task Later_operations_recheck_test_controlled_revocation_and_hosts_do_not_share_state(string change)
    {
        await using var first = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        await using var second = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        Assert.NotSame(first.Evidence, second.Evidence);
        Assert.NotNull((await first.ProbeAsync(ChatModule.Social)).Actor);
        if (change == "block") first.Evidence.Block();
        if (change == "unmatch") first.Evidence.Unmatch();
        if (change == "eligibility") first.Evidence.RevokeEligibility();
        Assert.Null((await first.ProbeAsync(ChatModule.Social)).Actor);
        Assert.NotNull((await second.ProbeAsync(ChatModule.Social)).Actor);
        Assert.Equal(2, first.Evidence.Reads);
        Assert.Equal(1, second.Evidence.Reads);
    }

    [Fact]
    public async Task Updating_match_evidence_does_not_erase_an_existing_block()
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        host.Evidence.Block();
        host.Evidence.Unmatch();
        host.Evidence.SetFlag("HasMutualMatch", true);
        Assert.Null((await host.ProbeAsync(ChatModule.Social)).Actor);
        Assert.Equal(1, host.Evidence.Reads);
    }

    [Fact]
    public async Task Request_scopes_and_host_disposal_clear_owned_actor_and_evidence_state()
    {
        var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Learning);
        var first = await host.ProbeAsync(ChatModule.Learning);
        var second = await host.ProbeAsync(ChatModule.Learning);
        Assert.NotNull(first.Actor);
        Assert.NotSame(first.State, second.State);
        Assert.True(first.State.IsDisposed);
        Assert.Null(first.State.Actor);
        Assert.True(second.State.IsDisposed);
        Assert.Null(second.State.Actor);
        await host.DisposeAsync();
        await host.DisposeAsync();
        Assert.True(host.Evidence.IsDisposed);
        Assert.Null(host.Evidence.LastEvidence);
        Assert.Null(host.Evidence.User.UserId);
        Assert.Null(await host.Evidence.Policy.AuthorizeConversationAsync(ChatModule.Learning, ChatPrototypeTestHost.Conversation));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.ProbeAsync(ChatModule.Learning));
        await using var fresh = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Learning);
        Assert.NotNull((await fresh.ProbeAsync(ChatModule.Learning)).Actor);
    }

    [Fact]
    public async Task Setup_failure_is_redacted_and_does_not_return_a_fallback_host()
    {
        SetupOwnedState? owned = null;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ChatPrototypeTestHost.StartAsync(
            SyntheticActor.Social, services =>
            {
                services.AddSingleton(_ => owned = new SetupOwnedState());
                services.AddSingleton<IHostedService, FailingStartup>();
            }));
        Assert.NotNull(owned);
        Assert.True(owned.IsDisposed);
        Assert.Equal("Synthetic Chat host setup failed.", error.Message);
        Assert.Null(error.InnerException);
        await using var next = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Anonymous);
        Assert.Null((await next.ProbeAsync(ChatModule.Social)).Actor);
    }

    [Fact]
    public async Task Diagnostics_and_dependency_failures_do_not_disclose_private_values()
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        var allowed = await host.ProbeAsync(ChatModule.Social);
        var diagnostic = $"{host} {host.Identity} {host.Evidence} {allowed} {allowed.Actor} {allowed.State} {host.Evidence.LastEvidence}";
        foreach (var id in new[] { host.Identity!.Subject, host.Identity.Account, host.Identity.Profile })
            Assert.DoesNotContain(id.ToString(), diagnostic, StringComparison.Ordinal);
        host.Evidence.Transform = _ => throw new InvalidOperationException("private_dependency_secret");
        Assert.Null((await host.ProbeAsync(ChatModule.Social)).Actor);
        using var response = await host.Client.GetAsync("/unmapped");
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Production_registrations_have_no_test_fixture_or_chat_routes_and_deny_all_stays_closed()
    {
        var production = new ServiceCollection();
        production.AddAccounts(new ConfigurationBuilder().Build());
        production.AddNotifications();
        var testAssembly = typeof(ChatPrototypeTestHost).Assembly;
        Assert.DoesNotContain(production, descriptor => descriptor.ServiceType.Assembly == testAssembly
            || descriptor.ImplementationType?.Assembly == testAssembly
            || descriptor.ServiceType == typeof(IChatAccessAuthorizer));
        var api = typeof(Program).Assembly;
        Assert.DoesNotContain(api.GetReferencedAssemblies(), reference => reference.Name == testAssembly.GetName().Name);
        Assert.DoesNotContain(api.GetTypes(), type => type.Name.Contains("Chat", StringComparison.Ordinal)
            && type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ApiControllerAttribute), true).Length > 0);
        Assert.DoesNotContain(api.GetTypes(), type => type == typeof(ChatPrototypeTestHost));
        foreach (var module in Enum.GetValues<ChatModule>())
            Assert.Null(await new DenyAllChatAccessAuthorizer().AuthorizeConversationAsync(module, ChatPrototypeTestHost.Conversation));
    }
    private sealed class SetupOwnedState : IDisposable
    {
        internal bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FailingStartup(SetupOwnedState state) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Assert.False(state.IsDisposed);
            throw new InvalidOperationException("private_setup_secret");
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Trait("Step", "08B-2")]
    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public async Task Http_Anonymous_or_unknown_principal_is_401_for_every_operation(int actor)
    {
        await using var host = await ChatPrototypeTestHost.StartAsync((SyntheticActor)actor);
        host.Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "forged");
        foreach (var path in new[] { ChatPrototypeApiTests.Root() + "?pageSize=1", ChatPrototypeApiTests.Target(), ChatPrototypeApiTests.Target() + "/messages?pageSize=1" })
            await HttpError(await host.Client.GetAsync(path), HttpStatusCode.Unauthorized);
        await HttpError(await host.Client.PostAsJsonAsync(ChatPrototypeApiTests.Target() + "/messages", new ChatPrototypeSubmitDto("denied")), HttpStatusCode.Unauthorized);
        Assert.Equal(0, host.Scenario.MessageCount);
    }

    [Trait("Step", "08B-2")]
    [Theory]
    [InlineData("Learning")]
    [InlineData("SOCIAL")]
    [InlineData("4")]
    [InlineData("unknown")]
    [InlineData("learning")]
    [InlineData("hobbies")]
    public async Task Http_Noncanonical_or_cross_module_requests_are_unavailable(string module)
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        await HttpError(await host.Client.GetAsync(ChatPrototypeApiTests.Root(module) + "?pageSize=1"), HttpStatusCode.NotFound);
        await HttpError(await host.Client.PostAsJsonAsync(ChatPrototypeApiTests.Target(module) + "/messages", new ChatPrototypeSubmitDto("denied")), HttpStatusCode.NotFound);
        Assert.Equal(3, host.Scenario.MessageCount);
    }

    [Trait("Step", "08B-2")]
    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("conflicting")]
    [InlineData("foreign-profile")]
    [InlineData("wrong-owner")]
    [InlineData("underage")]
    [InlineData("opt-out")]
    [InlineData("no-match")]
    [InlineData("blocked")]
    [InlineData("unmatched")]
    [InlineData("ineligible")]
    [InlineData("revoked")]
    public async Task Http_Real_policy_denials_never_disclose_targets_or_append(string denial)
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        if (denial == "missing") host.Evidence.RemoveEvidence();
        if (denial == "stale") host.Evidence.StaleEvidence();
        if (denial == "conflicting") host.Evidence.ConflictingEvidence();
        if (denial == "foreign-profile") host.Evidence.Transform = evidence => { ChatSyntheticAuthorizationFixture.Set(evidence, "ModuleProfileId", Guid.Parse("49000000-0000-4000-8000-000000000003")); return evidence; };
        if (denial == "wrong-owner") host.Evidence.Transform = evidence => { ChatSyntheticAuthorizationFixture.Set(evidence, "ProfileAccountId", Guid.Empty); return evidence; };
        if (denial == "underage") host.Evidence.SetFlag("IsAdult", false);
        if (denial == "opt-out") host.Evidence.SetFlag("HasSocialOptIn", false);
        if (denial == "no-match") host.Evidence.SetFlag("HasMutualMatch", null);
        if (denial == "blocked") host.Evidence.Block();
        if (denial == "unmatched") host.Evidence.Unmatch();
        if (denial == "ineligible") host.Evidence.RevokeEligibility();
        if (denial == "revoked") host.Evidence.SetFlag("IsRevoked", true);
        await HttpError(await host.Client.GetAsync(ChatPrototypeApiTests.Target()), HttpStatusCode.NotFound);
        await HttpError(await host.Client.GetAsync(ChatPrototypeApiTests.Target() + "/messages?pageSize=1"), HttpStatusCode.NotFound);
        await HttpError(await host.Client.PostAsJsonAsync(ChatPrototypeApiTests.Target() + "/messages", new ChatPrototypeSubmitDto("denied")), HttpStatusCode.NotFound);
        var list = await ChatPrototypeApiTests.Read<ChatPrototypePage<ChatPrototypeConversationDto>>(await host.Client.GetAsync(ChatPrototypeApiTests.Root() + "?pageSize=1"));
        Assert.Empty(list.Items);
        Assert.Null(list.NextCursor);
        Assert.Equal(3, host.Scenario.MessageCount);
    }

    [Trait("Step", "08B-2")]
    [Fact]
    public async Task Http_Missing_foreign_and_nonparticipant_targets_share_identical_safe_404s()
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        string? expected = null;
        foreach (var id in new[] { ChatPrototypeScenario.Hidden, Guid.Parse("10000000-0000-4000-8000-000000000099") })
        {
            foreach (var suffix in new[] { "", "/messages?pageSize=1" })
            {
                using var response = await host.Client.GetAsync(ChatPrototypeApiTests.Target(conversation: id) + suffix);
                await HttpError(response, HttpStatusCode.NotFound);
                var body = await response.Content.ReadAsStringAsync();
                expected ??= body;
                Assert.Equal(expected, body);
            }
            await HttpError(await host.Client.PostAsJsonAsync(ChatPrototypeApiTests.Target(conversation: id) + "/messages", new ChatPrototypeSubmitDto("denied")), HttpStatusCode.NotFound);
        }
        Assert.Equal(3, host.Scenario.MessageCount);
    }

    [Trait("Step", "08B-2")]
    [Fact]
    public async Task Http_Forged_sender_account_profile_identifiers_and_timestamp_cannot_change_server_values()
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Learning);
        host.Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-real-jwt");
        host.Client.DefaultRequestHeaders.Add("X-Account-Id", Guid.Empty.ToString());
        var forgedId = Guid.Parse("59000000-0000-4000-8000-000000000001");
        var before = DateTimeOffset.UtcNow;
        var response = await host.Client.PostAsJsonAsync(ChatPrototypeApiTests.Target("learning") + "/messages", new
        {
            text = "server owned",
            accountId = Guid.Empty,
            moduleProfileId = Guid.Empty,
            sender = "Social",
            messageId = forgedId,
            createdAtUtc = "1900-01-01T00:00:00Z",
            module = "social",
            conversationId = ChatPrototypeScenario.Hidden
        });
        var result = await ChatPrototypeApiTests.Read<ChatPrototypeMessageDto>(response, HttpStatusCode.Created);
        Assert.True(result.IsOwnMessage);
        Assert.NotEqual(forgedId, result.MessageId);
        Assert.Equal("learning", result.Module);
        Assert.Equal(ChatPrototypeTestHost.Conversation, result.ConversationId);
        Assert.InRange(result.CreatedAtUtc, before, DateTimeOffset.UtcNow);
    }

    [Trait("Step", "08B-2")]
    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"text\":12}")]
    [InlineData("{\"text\":null}")]
    [InlineData("{\"text\":\"\"}")]
    [InlineData("{\"text\":\" \\t\"}")]
    public async Task Http_Malformed_JSON_and_invalid_text_return_400_without_append(string json)
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        await HttpError(await host.Client.PostAsync(ChatPrototypeApiTests.Target() + "/messages", new StringContent(json, Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest);
        Assert.Equal(3, host.Scenario.MessageCount);
    }

    [Trait("Step", "08B-2")]
    [Theory]
    [InlineData(1, "learning", "")]
    [InlineData(1, "learning", "happy")]
    [InlineData(1, "learning", " ")]
    [InlineData(2, "hobbies", "")]
    [InlineData(2, "hobbies", "happy")]
    [InlineData(2, "hobbies", "\U0001F642")]
    [InlineData(3, "social", "")]
    [InlineData(3, "social", "happy")]
    [InlineData(3, "social", " ")]
    [InlineData(3, "social", "\U0001F642")]
    public async Task Http_Every_non_null_MoodTone_category_is_rejected_by_the_domain(int actor, string module, string tone)
    {
        await using var host = await ChatPrototypeTestHost.StartAsync((SyntheticActor)actor);
        await HttpError(await host.Client.PostAsJsonAsync(ChatPrototypeApiTests.Target(module) + "/messages", new ChatPrototypeSubmitDto("valid text", tone)), HttpStatusCode.BadRequest);
        Assert.Equal(3, host.Scenario.MessageCount);
    }

    [Trait("Step", "08B-2")]
    [Theory]
    [InlineData("")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=-1")]
    [InlineData("?pageSize=17")]
    [InlineData("?pageSize=2147483648")]
    [InlineData("?pageSize=1.5")]
    [InlineData("?pageSize=1&pageSize=2")]
    [InlineData("?pageSize=1&cursor=")]
    [InlineData("?pageSize=1&cursor=malformed")]
    [InlineData("?pageSize=1&cursor=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("?pageSize=1&cursor=a&cursor=b")]
    public async Task Http_Invalid_pagination_is_rejected_for_both_page_routes(string query)
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        await HttpError(await host.Client.GetAsync(ChatPrototypeApiTests.Root() + query), HttpStatusCode.BadRequest);
        await HttpError(await host.Client.GetAsync(ChatPrototypeApiTests.Target() + "/messages" + query), HttpStatusCode.BadRequest);
    }

    [Trait("Step", "08B-2")]
    [Fact]
    public async Task Http_Cursors_are_opaque_bound_to_actor_module_conversation_listing_and_host()
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        await using var another = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        await using var otherActor = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Learning);
        var page = await ChatPrototypeApiTests.Read<ChatPrototypePage<ChatPrototypeMessageDto>>(await host.Client.GetAsync(ChatPrototypeApiTests.Target() + "/messages?pageSize=1"));
        var cursor = Assert.IsType<string>(page.NextCursor);
        Assert.Matches("^[0-9A-F]{64}$", cursor);
        foreach (var path in new[] { ChatPrototypeApiTests.Target(conversation: ChatPrototypeScenario.Second) + "/messages", ChatPrototypeApiTests.Root() })
            await HttpError(await host.Client.GetAsync(path + "?pageSize=1&cursor=" + cursor), HttpStatusCode.BadRequest);
        await HttpError(await another.Client.GetAsync(ChatPrototypeApiTests.Target() + "/messages?pageSize=1&cursor=" + cursor), HttpStatusCode.BadRequest);
        await HttpError(await otherActor.Client.GetAsync(ChatPrototypeApiTests.Target("learning") + "/messages?pageSize=1&cursor=" + cursor), HttpStatusCode.BadRequest);
        var list = await ChatPrototypeApiTests.Read<ChatPrototypePage<ChatPrototypeConversationDto>>(await host.Client.GetAsync(ChatPrototypeApiTests.Root() + "?pageSize=1"));
        await HttpError(await host.Client.GetAsync(ChatPrototypeApiTests.Target() + "/messages?pageSize=1&cursor=" + list.NextCursor), HttpStatusCode.BadRequest);
        foreach (var id in new[] { host.Identity!.Account, host.Identity.Profile, host.Identity.Subject })
            Assert.DoesNotContain(id.ToString("N"), cursor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("synthetic", cursor, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Step", "08B-2")]
    [Theory]
    [InlineData("block")]
    [InlineData("unmatch")]
    [InlineData("eligibility")]
    public async Task Http_Revocation_after_success_prevents_cursor_reads_detail_and_write(string change)
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        var page = await ChatPrototypeApiTests.Read<ChatPrototypePage<ChatPrototypeMessageDto>>(await host.Client.GetAsync(ChatPrototypeApiTests.Target() + "/messages?pageSize=1"));
        if (change == "block") host.Evidence.Block();
        if (change == "unmatch") host.Evidence.Unmatch();
        if (change == "eligibility") host.Evidence.RevokeEligibility();
        await HttpError(await host.Client.GetAsync(ChatPrototypeApiTests.Target() + "/messages?pageSize=1&cursor=" + page.NextCursor), HttpStatusCode.NotFound);
        await HttpError(await host.Client.GetAsync(ChatPrototypeApiTests.Target()), HttpStatusCode.NotFound);
        await HttpError(await host.Client.PostAsJsonAsync(ChatPrototypeApiTests.Target() + "/messages", new ChatPrototypeSubmitDto("denied")), HttpStatusCode.NotFound);
        var listing = await ChatPrototypeApiTests.Read<ChatPrototypePage<ChatPrototypeConversationDto>>(await host.Client.GetAsync(ChatPrototypeApiTests.Root() + "?pageSize=1"));
        Assert.Empty(listing.Items);
        Assert.Equal(3, host.Scenario.MessageCount);
    }

    [Trait("Step", "08B-2")]
    [Fact]
    public async Task Http_Deterministic_revocation_immediately_before_append_is_rechecked()
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        host.Scenario.BeforeAppend = _ => { host.Evidence.Block(); return Task.CompletedTask; };
        await HttpError(await host.Client.PostAsJsonAsync(ChatPrototypeApiTests.Target() + "/messages", new ChatPrototypeSubmitDto("denied")), HttpStatusCode.NotFound);
        Assert.Equal(3, host.Scenario.MessageCount);
        Assert.Equal(2, host.Evidence.Reads);
    }

    [Trait("Step", "08B-2")]
    [Fact]
    public async Task Http_Cancellation_at_controlled_pre_append_boundary_never_appends()
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Scenario.BeforeAppend = async token =>
        {
            entered.TrySetResult();
            try { await held.Task.WaitAsync(token); }
            finally { finished.TrySetResult(); }
        };
        using var cancellation = new CancellationTokenSource();
        var pending = host.Client.PostAsJsonAsync(ChatPrototypeApiTests.Target() + "/messages", new ChatPrototypeSubmitDto("cancelled"), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, host.Scenario.MessageCount);
    }

    [Trait("Step", "08B-2")]
    [Fact]
    public async Task Http_Unexpected_failures_use_sanitized_500_no_store_and_leave_state_unchanged()
    {
        await using var host = await ChatPrototypeTestHost.StartAsync(SyntheticActor.Social);
        host.Scenario.BeforeRead = () => throw new InvalidOperationException("private_secret_" + host.Identity!.Account);
        foreach (var path in new[] { ChatPrototypeApiTests.Root() + "?pageSize=1", ChatPrototypeApiTests.Target() })
        {
            using var response = await host.Client.GetAsync(path);
            await HttpError(response, HttpStatusCode.InternalServerError);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private_secret", body, StringComparison.Ordinal);
            Assert.DoesNotContain(host.Identity!.Account.ToString(), body, StringComparison.Ordinal);
        }
        await HttpError(await host.Client.PostAsJsonAsync(ChatPrototypeApiTests.Target() + "/messages", new ChatPrototypeSubmitDto("no append")), HttpStatusCode.InternalServerError);
        Assert.Equal(3, host.Scenario.MessageCount);
    }

    private static async Task HttpError(HttpResponseMessage response, HttpStatusCode status)
    {
        var error = await ChatPrototypeApiTests.Read<ChatPrototypeError>(response, status);
        Assert.Equal((int)status, error.Status);
        Assert.Equal(status switch { HttpStatusCode.BadRequest => "invalid_request", HttpStatusCode.Unauthorized => "unauthenticated", HttpStatusCode.NotFound => "unavailable", _ => "unexpected_failure" }, error.Code);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "code", "title", "status" }, json.RootElement.EnumerateObject().Select(property => property.Name));
    }
}
