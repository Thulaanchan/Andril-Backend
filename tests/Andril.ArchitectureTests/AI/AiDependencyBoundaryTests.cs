using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.AI.Providers;
using Andril.AI.Services;
using Andril.API.Controllers.AI;
using Andril.API.Extensions;
using Andril.API.Integrations.AI;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.ArchitectureTests.AI;

/// <summary>Executable type/assembly/IL boundaries, not file-name discovery assertions.
/// Dynamic reflection/externally configured code and database concurrency remain review risks.</summary>
public sealed class AiDependencyBoundaryTests
{
    private static readonly Assembly Ai = typeof(AiChatService).Assembly;
    private static readonly Assembly Api = typeof(AiChatController).Assembly;
    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(c => c.Value);

    // Decode actual IL operands; scanning arbitrary byte values as tokens would give false hits.
    private static IEnumerable<MethodBase> Calls(Type type)
    {
        var types = new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
        foreach (var t in types)
        foreach (var method in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MethodBase>()
            .Concat(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)))
        {
            var bytes = method.GetMethodBody()?.GetILAsByteArray(); if (bytes is null) continue;
            for (var i = 0; i < bytes.Length;)
            {
                short value = bytes[i++]; if (value == 0xfe) value = (short)(0xfe00 | bytes[i++]);
                var code = Codes[value]; var offset = i;
                var size = code.OperandType switch
                {
                    OperandType.InlineNone => 0, OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, i), _ => 4
                };
                i += size;
                if (code.OperandType != OperandType.InlineMethod) continue;
                MethodBase? target = null;
                try { target = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, offset), t.IsGenericType ? t.GetGenericArguments() : null,
                    method.IsGenericMethod ? method.GetGenericArguments() : null); }
                catch (ArgumentException) { }
                if (target is not null) yield return target;
            }
        }
    }

    [Fact]
    public void ControllerCodeCannotInvokeRawHttpTransportSettlementOrLegacyWrites()
    {
        var calls = Calls(typeof(AiChatController)).ToArray(); Assert.NotEmpty(calls);
        Assert.Contains(calls, c => c.DeclaringType == typeof(AiConversationLifecycleService));
        Assert.DoesNotContain(calls, c => c.DeclaringType == typeof(HttpClient) || c.DeclaringType == typeof(IAiResponseProvider) ||
            c.DeclaringType?.Namespace?.StartsWith("Andril.AI.Providers", StringComparison.Ordinal) == true ||
            c.DeclaringType == typeof(AiBudgetService));
        Assert.DoesNotContain(calls, c => c.DeclaringType == typeof(AiConversationLifecycleService) &&
            c.Name is "StartConversationAsync" or "ContinueConversationAsync" or "SaveAssistantReplyAsync");
        foreach (var controller in Api.GetTypes().Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t)))
            Assert.DoesNotContain(Calls(controller), c => c.DeclaringType?.FullName == "Andril.AI.Providers.OpenAi.OpenAiResponsesHttpTransport");
    }

    [Fact]
    public void TransportAndGenerationInternalsHaveNoPublicExecutionOrExtraFriendAssembly()
    {
        var transport = Ai.GetType("Andril.AI.Providers.OpenAi.OpenAiResponsesHttpTransport", true)!;
        Assert.False(transport.IsVisible); Assert.True(transport.IsSealed);
        Assert.False(typeof(IAiResponseProvider).IsAssignableFrom(transport));
        Assert.Empty(transport.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        foreach (var type in Ai.GetTypes().Where(t => t.Name is "AiTextResponseProvider" or "AiGenerationService" or "OpenAiResponsesResultMapper"))
            Assert.False(type.IsVisible);
        Assert.Equal(new[] { "Andril.UnitTests" }, Ai.GetCustomAttributes<InternalsVisibleToAttribute>().Select(x => x.AssemblyName).ToArray());
        var mapper = Ai.GetType("Andril.AI.Providers.OpenAi.OpenAiResponsesResultMapper", true)!;
        Assert.DoesNotContain(Calls(mapper), c => c.DeclaringType == typeof(AiBudgetService) ||
            c.Name == "Settle" || c.DeclaringType == typeof(AiLateReplyCommitService));
    }

    [Fact]
    public void LegacyWriteMethodsContainNoRepositoryOrAlternativeMutationCalls()
    {
        var calls = Calls(typeof(AiConversationLifecycleService)).ToArray();
        Assert.Contains(calls, c => c.DeclaringType == typeof(IAiConversationRepository) && c.Name == "DeleteAsync");
        Assert.DoesNotContain(calls, c => c.DeclaringType == typeof(IAiConversationRepository) &&
            c.Name is "CreateWithUserMessageAsync" or "AppendUserMessageAsync" or "AppendAssistantMessageAsync");
        Assert.DoesNotContain(calls, c => c.DeclaringType == typeof(IAiOperationRepository) && c.Name == "AdmitAsync");
    }

    private sealed class Actor : IAiActorAuthorization
    {
        // One trusted synthetic identity per fixture instance, stable across admission/release.
        private readonly Guid subject = Guid.NewGuid();
        private readonly Guid profile = Guid.NewGuid();
        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(AiModule module, CancellationToken t = default) =>
            Task.FromResult<AiAuthorizedActor?>(new(subject, profile, module));
    }
    private sealed class Provider : IAiResponseProvider
    {
        internal int Calls;
        public Task<AiProviderResponse> GenerateAsync(AiProviderPrompt prompt, CancellationToken t = default)
        { Calls++; return Task.FromResult(new AiProviderResponse("Uncommitted candidate", false)); }
    }
    [Fact]
    public async Task SealedBuiltInMockIsTheOnlyLegacyExecutableProvider()
    {
        var spy = new Provider(); await Assert.ThrowsAsync<InvalidOperationException>(() => new AiChatService(new Actor(), spy).ReplyAsync(AiModule.Learning, "Synthetic"));
        Assert.Equal(0, spy.Calls); Assert.True(typeof(MockAiResponseProvider).IsSealed);
        var reply = await new AiChatService(new Actor(), new MockAiResponseProvider()).ReplyAsync(AiModule.Learning, "Synthetic");
        Assert.True(reply.IsMock);
    }

    [Fact]
    public void ActualProductionAssemblyGraphHasNoTestDependencyOrCycle()
    {
        var assemblies = new Dictionary<string, Assembly>(); var queue = new Queue<Assembly>(new[] { Api, Ai });
        while (queue.TryDequeue(out var a))
        {
            if (!assemblies.TryAdd(a.GetName().Name!, a)) continue;
            foreach (var reference in a.GetReferencedAssemblies().Where(r => r.Name!.StartsWith("Andril.", StringComparison.Ordinal)))
            { Assert.DoesNotContain("Tests", reference.Name!); queue.Enqueue(Assembly.Load(reference)); }
        }
        var visiting = new HashSet<string>(); var visited = new HashSet<string>();
        void Visit(string name)
        {
            if (visited.Contains(name)) return;
            Assert.True(visiting.Add(name), "Production assembly dependency cycle: " + name);
            foreach (var reference in assemblies[name].GetReferencedAssemblies().Where(r => assemblies.ContainsKey(r.Name!))) Visit(reference.Name!);
            visiting.Remove(name); visited.Add(name);
        }
        foreach (var name in assemblies.Keys) Visit(name);
        // An unused ProjectReference can be omitted from compiled AssemblyRef metadata.
        // Enforce the permitted direction without requiring a spurious runtime dependency.
        Assert.All(Ai.GetReferencedAssemblies().Where(r => r.Name!.StartsWith("Andril.")), r => Assert.Equal("Andril.Shared", r.Name));
    }

    [Fact]
    public void ActualMvcAndDisabledServiceGraphContainNoAiActivationOrMockFallback()
    {
        var services = new ServiceCollection(); services.AddLogging();
        var mvc = services.AddControllers().AddApplicationPart(Api); var feature = new ControllerFeature(); mvc.PartManager.PopulateFeature(feature);
        Assert.DoesNotContain(feature.Controllers, t => t.AsType() == typeof(AiChatController));
        services.AddDisabledAiFoundation();
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(AiMaintenanceWorker) || d.ServiceType == typeof(IAiResponseProvider) ||
            d.ImplementationType == typeof(MockAiResponseProvider) || d.ImplementationType?.Assembly == Ai);
        var entry = Api.EntryPoint!;
        var calls = Calls(entry.DeclaringType!).ToArray();
        Assert.DoesNotContain(calls, c => c.DeclaringType == typeof(AiServiceCollectionExtensions) ||
            c.DeclaringType == typeof(AiMaintenanceWorker) || c.DeclaringType == typeof(AiChatController));
    }
}
