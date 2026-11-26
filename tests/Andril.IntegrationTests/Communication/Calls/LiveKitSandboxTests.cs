using Andril.API.Development.Calls;
using Andril.Communication.Calls.Development;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Andril.IntegrationTests.Communication.Calls;

public sealed class LiveKitSandboxFactAttribute : FactAttribute
{
    public LiveKitSandboxFactAttribute()
    {
        if (System.Environment.GetEnvironmentVariable("ANDRIL_RUN_LIVEKIT_SANDBOX") != "true")
            Skip = "Actual LiveKit sandbox connection NOT EXECUTED; explicit opt-in required.";
    }
}
public sealed class LiveKitSandboxTests
{
    private sealed class SandboxEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "ChatRealtimeDevelopment";
        public string ApplicationName { get; set; } = "D12Sandbox";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    [LiveKitSandboxFact]
    public async Task D12ActualSandboxRoomTokensInspectionAndCleanup()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables("ANDRIL_").Build();
        var environment = new SandboxEnvironment();
        var options = LiveKitSandboxConfiguration.Load(configuration, environment);
        using var client = LiveKitSandboxConfiguration.CreateClient();
        var adapter = new LiveKitDevelopmentAdapter(client, options, environment);
        var room = new CallProviderReference("cr_" + Guid.NewGuid().ToString("N"),
            "cp_" + Guid.NewGuid().ToString("N"), "cp_" + Guid.NewGuid().ToString("N"));
        try
        {
            await adapter.CreateOrGetRoomAsync(room, default);
            Assert.True((await adapter.InspectAsync(room, default)).RoomExists);
            var caller = await adapter.IssueCredentialAsync(room, room.CallerIdentity, CallType.Audio, TimeSpan.FromMinutes(1), default);
            var callee = await adapter.IssueCredentialAsync(room, room.CalleeIdentity, CallType.Audio, TimeSpan.FromMinutes(1), default);
            Assert.NotEqual(caller.Token, callee.Token);
            Assert.Equal(room.CallerIdentity, caller.Identity);
            Assert.Equal(room.CalleeIdentity, callee.Identity);
        }
        finally
        {
            // Also attempted after an uncertain CreateRoom acknowledgement. Never enumerate/delete unrelated rooms.
            try
            {
                try { await adapter.RemoveParticipantAsync(room, room.CallerIdentity, default); }
                finally { await adapter.RemoveParticipantAsync(room, room.CalleeIdentity, default); }
            }
            finally { await adapter.CloseRoomAsync(room, default); }
        }
        Assert.False((await adapter.InspectAsync(room, default)).RoomExists);
    }
}
