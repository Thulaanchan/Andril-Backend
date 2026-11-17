using Andril.API.Authorization.Admin;
using Xunit;

namespace Andril.SecurityTests.Authorization.Admin;

public sealed class DenyAllAdminPermissionReaderTests
{
    private readonly DenyAllAdminPermissionReader _reader = new();

    [Theory]
    [InlineData("Moderation.Read", null)]
    [InlineData("Finance.Read", null)]
    [InlineData("Map.Review", "hobbies")]
    [InlineData("Groups.Review", "learning")]
    [InlineData("Safety.Review", "social")]
    public async Task HasPermissionAsync_AlwaysDenies(
        string permission, string? moduleScope)
    {
        var allowed = await _reader.HasPermissionAsync(
            "11111111-1111-4111-8111-111111111111",
            permission,
            moduleScope);

        Assert.False(allowed);
    }

    [Fact]
    public async Task HasPermissionAsync_InvalidSubjectStillDenies()
    {
        var allowed = await _reader.HasPermissionAsync(
            "invalid-subject", "Moderation.Read", null);

        Assert.False(allowed);
    }

    [Fact]
    public async Task HasPermissionAsync_CancelledTokenDoesNotGrant()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            var allowed = await _reader.HasPermissionAsync(
                "11111111-1111-4111-8111-111111111111",
                "Moderation.Read", null, cts.Token);

            Assert.False(allowed);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is also a non-granting outcome.
        }
    }
}
