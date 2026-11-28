using System.Reflection;
using System.Reflection.Emit;
using System.Security.Claims;
using Andril.API.Authentication;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.Entities;
using Andril.Learning.CV;
using Andril.API.Extensions;
using Andril.Persistence.Repositories.Common;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andril.ArchitectureTests.Dependencies;

/// <summary>
/// TEMPORARY pending-activation gates, not permanent prohibitions on Learning features.
/// Revise these gates with approved M2/Learning activation and its authorization/API tests.
/// Inspect compiled metadata and invoke existing registration methods only: never run Program,
/// execute the internal setup simulation, resolve a DbContext, start HTTP or contact a provider.
/// IL checks cover static references, not arbitrary reflection or externally injected code.
/// </summary>
public sealed class LearningFoundationBoundaryTests
{
    private static readonly Assembly Api = typeof(Program).Assembly;
    private static readonly Assembly Learning = typeof(LearningPreferences).Assembly;
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    [Fact]
    public void SetupEvaluatorRemainsInternalStaticAndHasNoStaticIoOrAuthorizationDependencies()
    {
        var evaluator = Learning.GetType("Andril.Learning.Services.LearningSetupEvidenceEvaluator", throwOnError: true)!;
        Assert.False(evaluator.IsVisible);
        Assert.True(evaluator.IsAbstract && evaluator.IsSealed);
        Assert.Empty(evaluator.GetFields(Declared));
        var evaluate = Assert.Single(evaluator.GetMethods(Declared), method => method.Name == "Evaluate");
        Assert.True(evaluate.IsAssembly && evaluate.IsStatic);
        Assert.False(evaluate.ReturnType.IsVisible);
        Assert.False(evaluate.GetParameters()[0].ParameterType.IsVisible);

        // Follow Learning method references without invoking them, including generated helpers.
        var pending = new Queue<Type>([evaluator]);
        var visited = new HashSet<Type>();
        var inspected = new List<MethodBase>();
        while (pending.TryDequeue(out var type))
        {
            if (!visited.Add(type)) continue;
            foreach (var method in Methods(type))
            foreach (var instruction in Instructions(method))
            {
                if (instruction.Operand is not MethodBase target) continue;
                inspected.Add(target);
                if (target.DeclaringType?.Assembly == Learning) pending.Enqueue(target.DeclaringType);
            }
        }
        Assert.Contains(inspected, method => method.DeclaringType == typeof(LearningSkillCatalogue) && method.Name == "ValidateNewSelections");
        Assert.DoesNotContain(inspected, method => method.DeclaringType is { } type &&
            (type.Namespace?.StartsWith("System.Net", StringComparison.Ordinal) == true ||
             type.Namespace?.StartsWith("System.IO", StringComparison.Ordinal) == true ||
             type.Namespace?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true ||
             type.Assembly == Api || type.Assembly == typeof(LearningDbContext).Assembly ||
             type.Assembly == typeof(IPrivateModuleAccess).Assembly));
    }

    [Fact]
    public void ProductionLearningCompositionIsDefaultDisabledAndOnlyPrivateCvContractsAreApproved()
    {
        var calls = ProductionApiCalls();
        Assert.DoesNotContain(calls.SelectMany(MemberTypes), type => IsLearningBoundaryType(type) && !IsApprovedPrivateCvContract(type));
        var services = ProductionServices();
        Assert.DoesNotContain(services, descriptor => IsLearningBoundaryType(descriptor.ServiceType) ||
            (descriptor.ImplementationType is { } implementation && IsLearningBoundaryType(implementation)) ||
            (descriptor.ImplementationInstance is { } instance && IsLearningBoundaryType(instance.GetType())));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(DbContextOptions<LearningDbContext>));
    }

    [Fact]
    public void ExplicitPrivateCvCompositionKeepsRealM2AuthorityAndNoOtherLearningRuntime()
    {
        var services = ProductionServices(PrivateCvConfiguration(true));
        var cv = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ILearningCvRepository));
        Assert.Equal(ServiceLifetime.Scoped, cv.Lifetime);
        Assert.NotNull(cv.ImplementationFactory);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType != typeof(ILearningCvRepository) &&
            (IsLearningBoundaryType(descriptor.ServiceType) ||
             descriptor.ImplementationType is { } implementation && IsLearningBoundaryType(implementation) ||
             descriptor.ImplementationInstance is { } instance && IsLearningBoundaryType(instance.GetType())));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(DbContextOptions<LearningDbContext>));
        Assert.Equal(typeof(PrivateProfileService), Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPrivateProfileService)).ImplementationType);
        Assert.Equal(typeof(PrivateProfileRepository), Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPrivateProfileRepository)).ImplementationType);
        // The new purpose-specific document callback does not silently activate the generic module gate.
        Assert.Equal(typeof(PrivateModuleAccess), Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPrivateModuleAccess)).ImplementationType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateCvEndpointsAreAbsentUnlessEnabledAndNeverAnonymous(bool enabled)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Services.AddAuthorization();
        builder.Services.AddVerifiedLearningPrivateCv(PrivateCvConfiguration(enabled));
        await using var app = builder.Build();
        app.MapVerifiedLearningPrivateCv();
        // Inspect real endpoint metadata without starting HTTP or resolving persistence.
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        if (!enabled) { Assert.Empty(endpoints); return; }
        var expected = new[]
        {
            "GET /api/learning/cvs", "POST /api/learning/cvs",
            "PUT /api/learning/cvs/{id:guid}/contact-consent",
            "POST /api/learning/cvs/{id:guid}/sections/{category}/structured",
            "GET /api/learning/cvs/{id:guid}/preview", "GET /api/learning/cvs/{id:guid}/pdf"
        };
        Assert.Equal(expected.Order(), endpoints.Select(endpoint =>
            Assert.Single(endpoint.Metadata.GetRequiredMetadata<HttpMethodMetadata>().HttpMethods) + " " + endpoint.RoutePattern.RawText!.TrimEnd('/')).Order());
        Assert.All(endpoints, endpoint =>
        {
            Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        });
    }

    private static IConfiguration PrivateCvConfiguration(bool enabled) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Learning:PrivateCv:Enabled"] = enabled.ToString(),
            ["Learning:PrivateCv:RuntimeRole"] = "architecture_metadata_only"
        }).Build();

    // Exact reviewed CV contracts only; no namespace/assembly-wide activation exception.
    [Fact]
    public void PrivateCvApprovalCannotAuthorizeOtherLearningPersistenceOrFoundationContracts()
    {
        foreach (var type in new[] { typeof(LearningDbContext), typeof(DbContextOptions<LearningDbContext>),
            typeof(LearningSkillSelection), typeof(IEnumerable<LearningSkillSelection>) })
            Assert.False(IsApprovedPrivateCvContract(type));
        Assert.True(IsApprovedPrivateCvContract(typeof(Task<ILearningCvRepository>)));
    }

    private static bool IsApprovedPrivateCvContract(Type type) =>
        type.HasElementType ? IsApprovedPrivateCvContract(type.GetElementType()!) :
        type.IsGenericType ? type.Assembly != Learning && type.GetGenericArguments().All(argument => !IsLearningBoundaryType(argument) || IsApprovedPrivateCvContract(argument)) :
        new[] { typeof(ILearningCvRepository), typeof(CvTemplate), typeof(CvOperationState), typeof(CvReadResult),
            typeof(CvListResult), typeof(CvPreviewResult), typeof(CvPdfResult), typeof(CvSaveChange),
            typeof(CvSectionCategory), typeof(CvSection), typeof(CvSection.Entry), typeof(CvStructuredFields),
            typeof(LearningCv), typeof(CvPreview), typeof(CvPdfDocument) }.Contains(type);

    [Fact]
    public void CurrentMvcContractsAndMinimalHttpMappingsDoNotExposeLearningFoundations()
    {
        using var provider = ProductionServices().BuildServiceProvider();
        var actions = provider.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>().ToArray();
        Assert.NotEmpty(actions);
        foreach (var action in actions)
        {
            Assert.False(action.ControllerTypeInfo.Namespace?.StartsWith("Andril.API.Controllers.Learning", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(action.MethodInfo.GetParameters().Select(parameter => parameter.ParameterType), IsLearningBoundaryType);
            Assert.False(IsLearningBoundaryType(action.MethodInfo.ReturnType));
        }

        // MVC inspection cannot detect minimal APIs. Keep the existing single health mapping
        // as an explicit temporary gate; approved new minimal mappings must update this test.
        var root = Api.EntryPoint!.DeclaringType!;
        var rootInstructions = Methods(root).SelectMany(Instructions).ToArray();
        var httpMaps = rootInstructions.Select(instruction => instruction.Operand).OfType<MethodBase>()
            .Where(method => method.DeclaringType?.Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true &&
                method.Name is "MapGet" or "MapPost" or "MapPut" or "MapPatch" or "MapDelete" or "MapMethods" or "MapGroup").ToArray();
        Assert.Equal("MapGet", Assert.Single(httpMaps).Name);
        Assert.Contains(rootInstructions, instruction => instruction.Code == OpCodes.Ldstr && Equals(instruction.Operand, "/health"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingM2ModuleAccessCannotBeGrantedByACompletionBoolean(bool complete)
    {
        var services = ProductionServices();
        var registration = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPrivateModuleAccess));
        Assert.Equal(typeof(PrivateModuleAccess), registration.ImplementationType);
        var completion = new CompletionProbe(complete);
        // Test-only completion evidence, not an actor or production authorization adapter.
        services.AddSingleton<IPrivateProfileCompletionReader>(completion);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<IPrivateModuleAccess>();
        Assert.IsType<PrivateModuleAccess>(access);
        await Assert.ThrowsAsync<ProfileContractPendingException>(() => access.RequireAsync("learning"));
        Assert.Equal(1, completion.Calls);
        Assert.Equal("learning", completion.Module);
    }

    [Fact]
    public async Task ProductionIdentityUsesRealBearerValidationAndDeniesUnverifiedIdentity()
    {
        var services = ProductionServices();
        var actorRegistration = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ICurrentUser));
        Assert.Equal(typeof(HttpCurrentUser), actorRegistration.ImplementationType);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var savedContext = accessor.HttpContext;
        try
        {
            accessor.HttpContext = new DefaultHttpContext();
            var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
            Assert.IsType<HttpCurrentUser>(user);
            Assert.False(user.IsAuthenticated);
            Assert.Null(user.UserId);
            accessor.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", "00000000-0000-0000-0000-000000000001"),
                new Claim("role", "authenticated"), new Claim("is_anonymous", "false")]));
            Assert.False(user.IsAuthenticated);
            Assert.Null(user.UserId);

            var schemes = await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync();
            var bearer = Assert.Single(schemes);
            Assert.Equal(JwtBearerDefaults.AuthenticationScheme, bearer.Name);
            Assert.Equal(typeof(JwtBearerHandler), bearer.HandlerType);
            var authorization = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
            Assert.NotNull(authorization.FallbackPolicy);
            Assert.Contains(authorization.FallbackPolicy.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);

            // Empty local configuration must fail closed before any JWKS/network request.
            var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(bearer.Name);
            var context = new MessageReceivedContext(accessor.HttpContext, bearer, options);
            await options.Events.MessageReceived(context);
            Assert.NotNull(context.Result?.Failure);
        }
        finally { accessor.HttpContext = savedContext; }
    }

    private sealed class CompletionProbe(bool complete) : IPrivateProfileCompletionReader
    {
        internal int Calls { get; private set; }
        internal string? Module { get; private set; }
        public Task<ProfileCompletionStatus> ReadAsync(string module, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++; Module = module;
            return Task.FromResult(new ProfileCompletionStatus(complete ? null : "approved_profile_contract", complete));
        }
    }

    private static bool IsLearningBoundaryType(Type type) => type.Assembly == Learning || type == typeof(LearningDbContext) ||
        (type.HasElementType && IsLearningBoundaryType(type.GetElementType()!)) ||
        (type.IsGenericType && type.GetGenericArguments().Any(IsLearningBoundaryType));

    private static ServiceCollection ProductionServices(IConfiguration? suppliedConfiguration = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(); services.AddControllers().AddApplicationPart(Api); services.AddProblemDetails();
        var configuration = suppliedConfiguration ?? new ConfigurationBuilder().Build();
        // Discover and invoke the actual API-owned service-registration calls in Program.
        // No copied list of registrations can silently omit a newly added extension.
        var registrations = Methods(Api.EntryPoint!.DeclaringType!).SelectMany(Instructions)
            .Select(instruction => instruction.Operand).OfType<MethodInfo>()
            .Where(method => method.DeclaringType?.Assembly == Api && method.IsStatic &&
                method.GetParameters().FirstOrDefault()?.ParameterType == typeof(IServiceCollection)).Distinct().ToArray();
        Assert.Contains(registrations, method => method.Name == "AddAccounts");
        Assert.Contains(registrations, method => method.Name == "AddSupabaseAuthentication");
        foreach (var registration in registrations)
        {
            var parameters = registration.GetParameters();
            Assert.True(parameters.Length is 1 or 2, "Review new production registration signature before changing this temporary gate.");
            object?[] arguments;
            if (parameters.Length == 1) arguments = [services];
            else if (parameters[1].ParameterType == typeof(IConfiguration)) arguments = [services, configuration];
            else
            {
                // The OpenAPI source generator emits an interceptor into the API assembly.
                // Keep it in discovery and supply Program's real callback, not an empty substitute.
                Assert.Equal("AddOpenApi", registration.Name);
                Assert.Equal(typeof(Action<OpenApiOptions>), parameters[1].ParameterType);
                arguments = [services, ProductionOpenApiCallback()];
            }
            registration.Invoke(null, arguments);
        }
        return services;
    }

    private static Action<OpenApiOptions> ProductionOpenApiCallback()
    {
        var callback = Assert.Single(Methods(Api.EntryPoint!.DeclaringType!).OfType<MethodInfo>(), method =>
            method.ReturnType == typeof(void) && method.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual([typeof(OpenApiOptions)]));
        if (callback.IsStatic) return callback.CreateDelegate<Action<OpenApiOptions>>();

        // Program's noncapturing lambda is an instance method on its generated singleton.
        // Resolve the singleton by metadata shape; do not execute Program or guess a generated name.
        var declaringType = callback.DeclaringType!;
        var singleton = Assert.Single(declaringType.GetFields(Declared), field =>
            field.IsStatic && field.FieldType == declaringType);
        var target = singleton.GetValue(null);
        Assert.NotNull(target);
        return callback.CreateDelegate<Action<OpenApiOptions>>(target);
    }

    private static MemberInfo[] ProductionApiCalls()
    {
        var pending = new Queue<Type>([Api.EntryPoint!.DeclaringType!]);
        var visited = new HashSet<Type>();
        var members = new List<MemberInfo>();
        while (pending.TryDequeue(out var type))
        {
            if (!visited.Add(type)) continue;
            foreach (var member in Methods(type).SelectMany(Instructions).Select(instruction => instruction.Operand).OfType<MemberInfo>())
            {
                members.Add(member);
                if (member is MethodBase method && method.DeclaringType?.Assembly == Api) pending.Enqueue(method.DeclaringType);
            }
        }
        return members.ToArray();
    }

    private static IEnumerable<Type> MemberTypes(MemberInfo member) => member switch
    {
        Type type => [type],
        MethodInfo method => new[] { method.DeclaringType!, method.ReturnType }.Concat(method.GetGenericArguments())
            .Concat(method.GetParameters().Select(parameter => parameter.ParameterType)),
        ConstructorInfo constructor => new[] { constructor.DeclaringType! }.Concat(constructor.GetParameters().Select(parameter => parameter.ParameterType)),
        FieldInfo field => [field.DeclaringType!, field.FieldType],
        _ => []
    };

    private static IEnumerable<MethodBase> Methods(Type type) => type.GetMethods(Declared).Cast<MethodBase>()
        .Concat(type.GetConstructors(Declared)).Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(Methods));

    // Follow the existing architecture suite's IL operand decoding convention. No byte/token
    // substring searches or silent resolution failures; metadata problems fail the test.
    private static IEnumerable<(OpCode Code, object? Operand)> Instructions(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null) yield break;
        for (var i = 0; i < bytes.Length;)
        {
            short value = bytes[i++];
            if (value == 0xfe) value = (short)(0xfe00 | bytes[i++]);
            var code = Codes[value];
            object? operand = null;
            if (code.OperandType == OperandType.InlineString) operand = method.Module.ResolveString(BitConverter.ToInt32(bytes, i));
            else if (code.OperandType is OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineField or OperandType.InlineTok)
                operand = method.Module.ResolveMember(BitConverter.ToInt32(bytes, i),
                    method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null,
                    method.IsGenericMethod ? method.GetGenericArguments() : null)
                    ?? throw new InvalidOperationException("Architecture metadata resolution failed.");
            i += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, i),
                _ => 4
            };
            yield return (code, operand);
        }
    }
}
