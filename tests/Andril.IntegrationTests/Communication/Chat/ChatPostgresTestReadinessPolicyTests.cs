using System.Reflection;
using Xunit;

namespace Andril.IntegrationTests.Communication.Chat;

public sealed class ChatPostgresTestReadinessPolicyTests
{
    private static ChatPostgresTestMetadata Explicit() => new(
        ChatTestEnvironment.IsolatedDisposable, "127.0.0.1", 15432, "synthetic_chat_test",
        ChatTestEnvironment.IsolatedDisposable, "synthetic_migrator", "synthetic_runtime",
        "synthetic_owner", ChatTestLifecycle.ExternallyManagedDisposable, ChatTestLifecycleApproval.Approved);

    [Theory]
    [InlineData("127.0.0.1", 1)]
    [InlineData("127.0.0.1", 65535)]
    [InlineData("::1", 15432)]
    public void Explicit_consistent_metadata_is_structurally_valid_but_never_authorizes_execution(string host, int port)
    {
        var result = ChatPostgresTestReadinessPolicy.Validate(Explicit() with { Host = host, Port = port });
        Assert.True(result.StructuralMetadataValid);
        Assert.True(result.ExternalLifecycleApprovalDeclared);
        Assert.True(result.HasCompleteDeclaredMetadata);
        Assert.Equal(ChatTestReadinessError.None, result.Error);
        AssertNoAuthority(result);
    }

    [Fact]
    public void Null_metadata_has_no_implicit_defaults()
    {
        var result = ChatPostgresTestReadinessPolicy.Validate(null);
        Assert.False(result.StructuralMetadataValid);
        Assert.False(result.ExternalLifecycleApprovalDeclared);
        Assert.Equal(ChatTestReadinessError.MissingMetadata, result.Error);
        AssertNoAuthority(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(99)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Missing_unknown_production_staging_and_shared_classifications_are_rejected(int? classification)
    {
        var value = classification.HasValue ? (ChatTestEnvironment?)classification.Value : null;
        AssertRejected(Explicit() with { Environment = value }, ChatTestReadinessError.InvalidEnvironment);
        AssertRejected(Explicit() with { DatabaseEnvironment = value }, ChatTestReadinessError.ConflictingDatabaseClassification);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void Port_is_explicit_and_bounded(int? port) =>
        AssertRejected(Explicit() with { Port = port }, ChatTestReadinessError.InvalidEndpoint);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("localhost.")]
    [InlineData("127.0.0.1 ")]
    [InlineData("127.1")]
    [InlineData("127.0.0.2")]
    [InlineData("[::1]")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("127.0.0.1:5432")]
    [InlineData("127.0.0.1,external.invalid")]
    [InlineData("/var/run/postgresql")]
    [InlineData("postgresql://example.invalid/test")]
    [InlineData("shared.supabase.invalid")]
    [InlineData("production.invalid")]
    [InlineData("Host=127.0.0.1;Password=synthetic_secret")]
    [InlineData("127.0.0.1\0")]
    public void Ambiguous_noncanonical_external_or_secret_like_endpoints_are_rejected_without_resolution(string? host) =>
        AssertRejected(Explicit() with { Host = host }, ChatTestReadinessError.InvalidEndpoint);

    public static IEnumerable<object[]> UnsafeIdentifiers()
    {
        string?[] values = [null, "", " ", "\t", " leading", "trailing ", "MixedCase", "two names",
            "one,two", "one.two", "quoted\"name", "name;DROP", "name=value", "name\\path", "name/path",
            "name\0tail", "name\ntail", "na\u200bme", "námé", "\ud800", "1name", new('a', 64),
            "postgres", "template0", "template1", "production", "staging", "shared", "supabase",
            "synthetic_prod_test", "synthetic_stage_test", "synthetic_password_test", "synthetic_pwd_test",
            "synthetic_secret_test", "synthetic_token_test", "synthetic_credentials_test", "synthetic_credential_test",
            "synthetic_connection_test", "synthetic_connectionstring_test", "synthetic_host_test", "synthetic_server_test",
            "synthetic_username_test", "Password=synthetic_secret", "Bearer synthetic_secret",
            "Host=example.invalid;Database=synthetic;Username=synthetic;Password=synthetic_secret"];
        foreach (var value in values)
            yield return [value!];
    }

    [Theory]
    [MemberData(nameof(UnsafeIdentifiers))]
    public void Unsafe_identity_metadata_is_rejected_in_every_identity_field_without_echoing_it(string? value)
    {
        AssertRejected(Explicit() with { Database = value }, ChatTestReadinessError.InvalidDatabaseIdentity);
        AssertRejected(Explicit() with { MigrationRole = value }, ChatTestReadinessError.InvalidRoleIdentity);
        AssertRejected(Explicit() with { RuntimeRole = value }, ChatTestReadinessError.InvalidRoleIdentity);
        AssertRejected(Explicit() with { ProvisioningAuthority = value }, ChatTestReadinessError.InvalidProvisioningAuthority);
    }

    [Fact]
    public void Canonical_identifiers_allow_digits_underscores_and_the_explicit_length_boundary()
    {
        var result = ChatPostgresTestReadinessPolicy.Validate(Explicit() with { Database = new string('a', 63), RuntimeRole = "synthetic_runtime_2" });
        Assert.True(result.HasCompleteDeclaredMetadata);
        AssertNoAuthority(result);
    }

    [Fact]
    public void Role_names_must_be_distinct_and_canonical_not_assumed_to_have_privileges()
    {
        AssertRejected(Explicit() with { RuntimeRole = "synthetic_migrator" }, ChatTestReadinessError.RolesNotSeparated);
        AssertRejected(Explicit() with { RuntimeRole = "SYNTHETIC_MIGRATOR" }, ChatTestReadinessError.InvalidRoleIdentity);
        var result = ChatPostgresTestReadinessPolicy.Validate(Explicit());
        AssertNoAuthority(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(99)]
    [InlineData(2)]
    [InlineData(3)]
    public void Missing_unknown_or_persistent_lifecycle_cannot_match_disposable_metadata(int? lifecycle) =>
        AssertRejected(Explicit() with { Lifecycle = lifecycle.HasValue ? (ChatTestLifecycle?)lifecycle.Value : null },
            ChatTestReadinessError.InvalidLifecycle);

    [Theory]
    [InlineData(null, (int)ChatTestReadinessError.ExternalApprovalNotDeclared)]
    [InlineData(2, (int)ChatTestReadinessError.ExternalApprovalNotDeclared)]
    [InlineData(3, (int)ChatTestReadinessError.ExternalApprovalRejected)]
    public void Missing_pending_or_rejected_external_approval_preserves_structural_distinction_but_not_complete_readiness(
        int? approval, int error)
    {
        var result = ChatPostgresTestReadinessPolicy.Validate(Explicit() with
        {
            ExternalLifecycleApproval = approval.HasValue ? (ChatTestLifecycleApproval?)approval.Value : null
        });
        Assert.True(result.StructuralMetadataValid);
        Assert.False(result.HasCompleteDeclaredMetadata);
        Assert.False(result.ExternalLifecycleApprovalDeclared);
        Assert.Equal((ChatTestReadinessError)error, result.Error);
        AssertNoAuthority(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(99)]
    public void Unknown_external_approval_status_fails_closed(int approval) =>
        AssertRejected(Explicit() with { ExternalLifecycleApproval = (ChatTestLifecycleApproval)approval },
            ChatTestReadinessError.InvalidExternalApprovalStatus);

    [Fact]
    public void Familiar_host_and_disposable_database_name_do_not_supply_missing_classification_or_approval()
    {
        AssertRejected(Explicit() with { Environment = null }, ChatTestReadinessError.InvalidEnvironment);
        AssertRejected(Explicit() with { DatabaseEnvironment = ChatTestEnvironment.Production }, ChatTestReadinessError.ConflictingDatabaseClassification);
        var result = ChatPostgresTestReadinessPolicy.Validate(Explicit() with { ExternalLifecycleApproval = null });
        Assert.False(result.HasCompleteDeclaredMetadata);
        AssertNoAuthority(result);
    }

    [Fact]
    public void Approval_declaration_remains_an_unverified_assertion_even_when_other_metadata_is_invalid()
    {
        var result = ChatPostgresTestReadinessPolicy.Validate(Explicit() with { Host = "external.invalid" });
        Assert.False(result.StructuralMetadataValid);
        Assert.True(result.ExternalLifecycleApprovalDeclared);
        Assert.False(result.HasCompleteDeclaredMetadata);
        AssertNoAuthority(result);
    }

    [Fact]
    public void Default_diagnostics_are_fixed_and_never_include_raw_input()
    {
        const string sensitive = "Host=sensitive.invalid;Username=private_identity;Password=synthetic_secret";
        var metadata = Explicit() with { Database = sensitive, ProvisioningAuthority = sensitive };
        var result = ChatPostgresTestReadinessPolicy.Validate(metadata);
        Assert.Equal(nameof(ChatPostgresTestMetadata), metadata.ToString());
        Assert.Equal(nameof(ChatPostgresTestReadinessResult), result.ToString());
        Assert.Equal(nameof(ChatTestReadinessError.InvalidDatabaseIdentity), result.Error.ToString());
        Assert.DoesNotContain(sensitive, $"{metadata} {result} {result.Error}", StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_and_concurrent_calls_preserve_input_and_produce_equal_results()
    {
        var metadata = Explicit();
        var copy = metadata with { };
        var expected = ChatPostgresTestReadinessPolicy.Validate(metadata);
        Parallel.For(0, 64, _ => Assert.Equal(expected, ChatPostgresTestReadinessPolicy.Validate(metadata)));
        Assert.Equal(copy, metadata);
        AssertNoAuthority(expected);
    }

    [Fact]
    public void Test_only_contract_has_no_optional_inputs_or_injected_execution_services()
    {
        Assert.True(typeof(ChatPostgresTestReadinessPolicy).IsNotPublic);
        Assert.Equal(typeof(ChatPostgresTestReadinessPolicyTests).Assembly, typeof(ChatPostgresTestMetadata).Assembly);
        var constructor = Assert.Single(typeof(ChatPostgresTestMetadata).GetConstructors(), item => item.GetParameters().Length == 10);
        Assert.All(constructor.GetParameters(), parameter => Assert.False(parameter.HasDefaultValue));
        var validate = typeof(ChatPostgresTestReadinessPolicy).GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(typeof(ChatPostgresTestMetadata), Assert.Single(validate.GetParameters()).ParameterType);
        Assert.Empty(typeof(ChatPostgresTestReadinessPolicy).GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        // These shape/determinism checks do not instrument system calls. Side-effect absence also
        // requires review of the small policy body: only local string, enum and value operations.
    }

    private static void AssertRejected(ChatPostgresTestMetadata metadata, ChatTestReadinessError error)
    {
        var result = ChatPostgresTestReadinessPolicy.Validate(metadata);
        Assert.False(result.StructuralMetadataValid);
        Assert.False(result.HasCompleteDeclaredMetadata);
        Assert.Equal(error, result.Error);
        AssertNoAuthority(result);
    }

    private static void AssertNoAuthority(ChatPostgresTestReadinessResult result)
    {
        Assert.True(result.RequiresIndependentEnvironmentVerification);
        Assert.False(result.ExternalLifecycleApprovalVerified);
        Assert.False(result.MayConnect);
        Assert.False(result.MayProvision);
        Assert.False(result.MayMigrate);
        Assert.False(result.MayCleanup);
        Assert.False(result.MayExecuteSql);
        Assert.False(result.MayChangePrivileges);
        Assert.False(result.MayDeleteTestData);
    }
}
