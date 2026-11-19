
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.API.Integrations.AI;
using Xunit;

namespace Andril.SecurityTests.AI;

/// <summary>
/// AI-BE-02 fail-closed placeholder tests.
/// These tests do not validate a real database-backed
/// module-profile reader.
/// </summary>
public sealed class DenyAllAiModuleProfileAccessReaderTests
{
    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public async Task ValidModule_AlwaysDeniesAccess(
        AiModule module)
    {
        var reader =
            new DenyAllAiModuleProfileAccessReader();

        var profileId =
            await reader.FindAuthorizedProfileIdAsync(
                Guid.NewGuid(), module);

        Assert.Null(profileId);
    }

    [Fact]
    public async Task EmptyAccountId_DoesNotGrantAccess()
    {
        var reader =
            new DenyAllAiModuleProfileAccessReader();

        var profileId =
            await reader.FindAuthorizedProfileIdAsync(
                Guid.Empty, AiModule.Learning);

        Assert.Null(profileId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public async Task UndefinedModule_DoesNotGrantAccess(
        int moduleValue)
    {
        var reader =
            new DenyAllAiModuleProfileAccessReader();

        var profileId =
            await reader.FindAuthorizedProfileIdAsync(
                Guid.NewGuid(),
                (AiModule)moduleValue);

        Assert.Null(profileId);
    }

    [Fact]
    public async Task CancelledToken_ThrowsInsteadOfReturningAResult()
    {
        var reader =
            new DenyAllAiModuleProfileAccessReader();

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
                await reader.FindAuthorizedProfileIdAsync(
                    Guid.NewGuid(),
                    AiModule.Social,
                    cancellation.Token));
    }

    [Fact]
    public async Task CancelledTokenWithEmptyAccountId_StillThrows()
    {
        var reader =
            new DenyAllAiModuleProfileAccessReader();

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
                await reader.FindAuthorizedProfileIdAsync(
                    Guid.Empty,
                    AiModule.Learning,
                    cancellation.Token));
    }

    [Fact]
    public async Task ReaderViaInterface_AlsoDeniesAccess()
    {
        IAiModuleProfileAccessReader reader =
            new DenyAllAiModuleProfileAccessReader();

        var profileId =
            await reader.FindAuthorizedProfileIdAsync(
                Guid.NewGuid(),
                AiModule.Hobbies,
                CancellationToken.None);

        Assert.Null(profileId);
    }
}

