using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Andril.Social.Personality;
using Xunit;

namespace Andril.UnitTests.Social;

/// <summary>
/// Focused unit and privacy contract tests for the <see cref="IQuestionnairePersistenceRepository"/> interface.
/// <para>
/// <b>Scope and Architectural Boundaries:</b>
/// <list type="bullet">
/// <item>
/// <description>
/// Verifies the two-method contract shape (<c>GetByIdAsync</c> and <c>SaveAsync</c>) and absence of removed methods (<c>GetByProfileIdAsync</c>).
/// </description>
/// </item>
/// <item>
/// <description>
/// Uses a private, test-only in-memory fake to validate profile-scoped retrieval, save correlation, cancellation observation,
/// and privacy preservation.
/// </description>
/// </item>
/// <item>
/// <description>
/// The test fake is NOT production persistence verification and does not establish database uniqueness, encryption,
/// transactions, concurrency control, or response replacement/retention lifecycles.
/// </description>
/// </item>
/// </list>
/// </para>
/// </summary>
public sealed class QuestionnaireRepositoryContractTests
{
    private static readonly Guid SyntheticResponseId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SyntheticProfileId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset SyntheticTimestampUtc = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static QuestionnaireDefinition ProductionV1Definition => QuestionnaireV1Definition.Create();

    private static QuestionnaireAnswerEntity CreateAnswer(
        string questionId,
        string optionId = "1",
        Guid? responseId = null,
        Guid? answerId = null) =>
        new(
            answerId ?? Guid.NewGuid(),
            responseId ?? SyntheticResponseId,
            questionId,
            optionId,
            SyntheticTimestampUtc);

    private static QuestionnaireResponseEntity CreateSampleResponse(
        Guid? responseId = null,
        Guid? profileId = null,
        bool completed = false)
    {
        var respId = responseId ?? SyntheticResponseId;
        var profId = profileId ?? SyntheticProfileId;
        var answers = completed
            ? Enumerable.Range(1, 12).Select(i => CreateAnswer($"q{i:D2}", "3", responseId: respId)).ToList()
            : new List<QuestionnaireAnswerEntity> { CreateAnswer("q01", "1", responseId: respId) };

        return QuestionnaireResponseEntity.Create(
            respId,
            profId,
            ProductionV1Definition,
            answers,
            skip: false,
            SyntheticTimestampUtc);
    }

    #region A. Interface Shape Tests

    [Fact]
    public void Interface_IsInterfaceType()
    {
        Assert.True(typeof(IQuestionnairePersistenceRepository).IsInterface);
    }

    [Fact]
    public void Interface_DeclaresExactlyTwoMethods()
    {
        var methods = typeof(IQuestionnairePersistenceRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.Equal(2, methods.Length);
        Assert.Contains(methods, m => m.Name == "GetByIdAsync");
        Assert.Contains(methods, m => m.Name == "SaveAsync");
    }

    [Fact]
    public void Interface_GetByIdAsync_Exists()
    {
        var method = typeof(IQuestionnairePersistenceRepository).GetMethod("GetByIdAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void Interface_SaveAsync_Exists()
    {
        var method = typeof(IQuestionnairePersistenceRepository).GetMethod("SaveAsync");
        Assert.NotNull(method);
    }

    [Fact]
    public void Interface_GetByProfileIdAsync_DoesNotExist()
    {
        var method = typeof(IQuestionnairePersistenceRepository).GetMethod("GetByProfileIdAsync");
        Assert.Null(method);
    }

    [Fact]
    public void Interface_GetByIdAsync_ReturnTypeIsTaskOfNullableEntity()
    {
        var method = typeof(IQuestionnairePersistenceRepository).GetMethod("GetByIdAsync");
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<QuestionnaireResponseEntity>), method.ReturnType);

        var nullabilityInfo = new NullabilityInfoContext().Create(method.ReturnParameter);
        Assert.NotNull(nullabilityInfo.GenericTypeArguments);
        Assert.Single(nullabilityInfo.GenericTypeArguments);
        Assert.Equal(NullabilityState.Nullable, nullabilityInfo.GenericTypeArguments[0].ReadState);
    }

    [Fact]
    public void Interface_SaveAsync_ReturnTypeIsTask()
    {
        var method = typeof(IQuestionnairePersistenceRepository).GetMethod("SaveAsync");
        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method.ReturnType);
    }

    [Fact]
    public void Interface_GetByIdAsync_ParameterTypesAndOrderAreExact()
    {
        var method = typeof(IQuestionnairePersistenceRepository).GetMethod("GetByIdAsync");
        Assert.NotNull(method);

        var parameters = method.GetParameters();
        Assert.Equal(3, parameters.Length);

        Assert.Equal("profileId", parameters[0].Name);
        Assert.Equal(typeof(Guid), parameters[0].ParameterType);

        Assert.Equal("responseId", parameters[1].Name);
        Assert.Equal(typeof(Guid), parameters[1].ParameterType);

        Assert.Equal("cancellationToken", parameters[2].Name);
        Assert.Equal(typeof(CancellationToken), parameters[2].ParameterType);
    }

    [Fact]
    public void Interface_SaveAsync_ParameterTypesAndOrderAreExact()
    {
        var method = typeof(IQuestionnairePersistenceRepository).GetMethod("SaveAsync");
        Assert.NotNull(method);

        var parameters = method.GetParameters();
        Assert.Equal(3, parameters.Length);

        Assert.Equal("profileId", parameters[0].Name);
        Assert.Equal(typeof(Guid), parameters[0].ParameterType);

        Assert.Equal("response", parameters[1].Name);
        Assert.Equal(typeof(QuestionnaireResponseEntity), parameters[1].ParameterType);

        Assert.Equal("cancellationToken", parameters[2].Name);
        Assert.Equal(typeof(CancellationToken), parameters[2].ParameterType);
    }

    [Fact]
    public void Interface_BothMethods_ExposeOptionalCancellationToken()
    {
        var getMethod = typeof(IQuestionnairePersistenceRepository).GetMethod("GetByIdAsync");
        Assert.NotNull(getMethod);
        var getCtParam = getMethod.GetParameters()[2];
        Assert.True(getCtParam.IsOptional);
        Assert.True(getCtParam.HasDefaultValue);
        Assert.True(getCtParam.DefaultValue is null || Equals(getCtParam.DefaultValue, default(CancellationToken)));

        var saveMethod = typeof(IQuestionnairePersistenceRepository).GetMethod("SaveAsync");
        Assert.NotNull(saveMethod);
        var saveCtParam = saveMethod.GetParameters()[2];
        Assert.True(saveCtParam.IsOptional);
        Assert.True(saveCtParam.HasDefaultValue);
        Assert.True(saveCtParam.DefaultValue is null || Equals(saveCtParam.DefaultValue, default(CancellationToken)));
    }

    [Fact]
    public void Interface_DoesNotExposeUnscopedEnumerationOrListAllOperations()
    {
        var methods = typeof(IQuestionnairePersistenceRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance);

        var prohibitedNames = new[] { "List", "All", "Find", "GetAll", "Enumerate", "Query", "Search" };

        foreach (var method in methods)
        {
            foreach (var prohibited in prohibitedNames)
            {
                Assert.DoesNotContain(prohibited, method.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    #endregion

    #region B & C. Profile-Scoped Retrieval Tests

    [Fact]
    public async Task GetByIdAsync_SavedResponse_RetrievedWithCorrectProfileAndResponseIds()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var response = CreateSampleResponse();

        await fake.SaveAsync(response.ProfileId, response);
        var retrieved = await fake.GetByIdAsync(response.ProfileId, response.Id);

        Assert.NotNull(retrieved);
        Assert.Equal(response.Id, retrieved.Id);
        Assert.Equal(response.ProfileId, retrieved.ProfileId);
    }

    [Fact]
    public async Task GetByIdAsync_UnknownResponseId_ReturnsNull()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var response = CreateSampleResponse();
        await fake.SaveAsync(response.ProfileId, response);

        var unknownResponseId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var result = await fake.GetByIdAsync(response.ProfileId, unknownResponseId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByIdAsync_CrossProfileRetrieval_ReturnsNull()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var profileA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var profileB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var response = CreateSampleResponse(profileId: profileA);

        await fake.SaveAsync(profileA, response);

        // Attempt cross-profile retrieval: response owned by profile A requested via profile B
        var crossProfileResult = await fake.GetByIdAsync(profileB, response.Id);

        Assert.Null(crossProfileResult);
    }

    [Fact]
    public async Task GetByIdAsync_TwoSeparateProfiles_RetainDistinctResponsesWithoutLeakage()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var profileA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var profileB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var responseA = CreateSampleResponse(responseId: Guid.NewGuid(), profileId: profileA);
        var responseB = CreateSampleResponse(responseId: Guid.NewGuid(), profileId: profileB, completed: true);

        await fake.SaveAsync(profileA, responseA);
        await fake.SaveAsync(profileB, responseB);

        var retrievedA = await fake.GetByIdAsync(profileA, responseA.Id);
        var retrievedB = await fake.GetByIdAsync(profileB, responseB.Id);

        Assert.NotNull(retrievedA);
        Assert.NotNull(retrievedB);
        Assert.Equal(profileA, retrievedA.ProfileId);
        Assert.Equal(profileB, retrievedB.ProfileId);
        Assert.Equal(responseA.Id, retrievedA.Id);
        Assert.Equal(responseB.Id, retrievedB.Id);
        Assert.Equal(QuestionnaireParticipationStatus.InProgress, retrievedA.Status);
        Assert.Equal(QuestionnaireParticipationStatus.Completed, retrievedB.Status);

        // Confirm cross-lookup yields null
        Assert.Null(await fake.GetByIdAsync(profileA, responseB.Id));
        Assert.Null(await fake.GetByIdAsync(profileB, responseA.Id));
    }

    [Fact]
    public async Task GetByIdAsync_PreservesOriginalResponseIdentityAndProfileAssociation()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var responseId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var profileId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var response = CreateSampleResponse(responseId: responseId, profileId: profileId);

        await fake.SaveAsync(profileId, response);
        var retrieved = await fake.GetByIdAsync(profileId, responseId);

        Assert.NotNull(retrieved);
        Assert.Equal(responseId, retrieved.Id);
        Assert.Equal(profileId, retrieved.ProfileId);
        Assert.Equal("v1", retrieved.Version);
        Assert.Equal(SyntheticTimestampUtc, retrieved.CreatedAtUtc);
    }

    [Fact]
    public async Task GetByIdAsync_RetrievalDoesNotMutateResponseData()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var response = CreateSampleResponse(completed: true);

        await fake.SaveAsync(response.ProfileId, response);

        var retrieved1 = await fake.GetByIdAsync(response.ProfileId, response.Id);
        var retrieved2 = await fake.GetByIdAsync(response.ProfileId, response.Id);

        Assert.NotNull(retrieved1);
        Assert.NotNull(retrieved2);
        Assert.Equal(12, retrieved1.Answers.Count);
        Assert.Equal(12, retrieved2.Answers.Count);
        for (var i = 0; i < 12; i++)
        {
            Assert.Equal(retrieved1.Answers[i].QuestionId, retrieved2.Answers[i].QuestionId);
            Assert.Equal(retrieved1.Answers[i].OptionId, retrieved2.Answers[i].OptionId);
        }
    }

    #endregion

    #region D. Profile-Scoped Save Tests

    [Fact]
    public async Task SaveAsync_ValidResponse_StoredSuccessfullyUnderMatchingProfile()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var response = CreateSampleResponse();

        await fake.SaveAsync(response.ProfileId, response);

        Assert.Equal(1, fake.StoredCount);
    }

    [Fact]
    public async Task SaveAsync_StoredResponse_CanBeRetrievedByCompositeReference()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var response = CreateSampleResponse();

        await fake.SaveAsync(response.ProfileId, response);
        var retrieved = await fake.GetByIdAsync(response.ProfileId, response.Id);

        Assert.Same(response, retrieved);
    }

    [Fact]
    public async Task SaveAsync_MismatchedProfileScope_ThrowsArgumentException()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var actualProfileId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var mismatchedProfileScope = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var response = CreateSampleResponse(profileId: actualProfileId);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            fake.SaveAsync(mismatchedProfileScope, response));

        Assert.Equal("response", ex.ParamName);
    }

    [Fact]
    public async Task SaveAsync_RejectedMismatchedSave_DoesNotModifyStoredData()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var originalProfileId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var initialResponse = CreateSampleResponse(profileId: originalProfileId);
        await fake.SaveAsync(originalProfileId, initialResponse);

        Assert.Equal(1, fake.StoredCount);

        var foreignProfile = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var mismatchedResponse = CreateSampleResponse(profileId: Guid.NewGuid());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fake.SaveAsync(foreignProfile, mismatchedResponse));

        Assert.Equal(1, fake.StoredCount);
        var retrieved = await fake.GetByIdAsync(originalProfileId, initialResponse.Id);
        Assert.Same(initialResponse, retrieved);
    }

    [Fact]
    public async Task SaveAsync_PreservesResponseIdUnchanged()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var expectedId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var response = CreateSampleResponse(responseId: expectedId);

        await fake.SaveAsync(response.ProfileId, response);
        var retrieved = await fake.GetByIdAsync(response.ProfileId, expectedId);

        Assert.NotNull(retrieved);
        Assert.Equal(expectedId, retrieved.Id);
    }

    [Fact]
    public async Task SaveAsync_DoesNotMutateOriginalResponseEntity()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var response = CreateSampleResponse(completed: true);

        var idBefore = response.Id;
        var profileIdBefore = response.ProfileId;
        var versionBefore = response.Version;
        var statusBefore = response.Status;
        var createdAtBefore = response.CreatedAtUtc;
        var answerCountBefore = response.Answers.Count;

        await fake.SaveAsync(response.ProfileId, response);

        Assert.Equal(idBefore, response.Id);
        Assert.Equal(profileIdBefore, response.ProfileId);
        Assert.Equal(versionBefore, response.Version);
        Assert.Equal(statusBefore, response.Status);
        Assert.Equal(createdAtBefore, response.CreatedAtUtc);
        Assert.Equal(answerCountBefore, response.Answers.Count);
    }

    [Fact]
    public async Task SaveAsync_NullResponse_ThrowsArgumentNullException()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            fake.SaveAsync(Guid.NewGuid(), null!));
    }

    [Fact]
    public async Task SaveAsync_EmptyProfileId_ThrowsArgumentException()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var response = CreateSampleResponse();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            fake.SaveAsync(Guid.Empty, response));

        Assert.Equal("profileId", ex.ParamName);
    }

    #endregion

    #region E. Cancellation Tests

    [Fact]
    public async Task GetByIdAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var response = CreateSampleResponse();
        await fake.SaveAsync(response.ProfileId, response);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fake.GetByIdAsync(response.ProfileId, response.Id, cts.Token));
    }

    [Fact]
    public async Task SaveAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var response = CreateSampleResponse();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fake.SaveAsync(response.ProfileId, response, cts.Token));
    }

    [Fact]
    public async Task SaveAsync_CancelledSave_DoesNotModifyStoredTestData()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var initialResponse = CreateSampleResponse();
        await fake.SaveAsync(initialResponse.ProfileId, initialResponse);

        Assert.Equal(1, fake.StoredCount);

        var newResponse = CreateSampleResponse(responseId: Guid.NewGuid());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fake.SaveAsync(newResponse.ProfileId, newResponse, cts.Token));

        Assert.Equal(1, fake.StoredCount);
        Assert.Null(await fake.GetByIdAsync(newResponse.ProfileId, newResponse.Id));
    }

    [Fact]
    public async Task Cancellation_DoesNotSilentlyConvertToNullOrSuccess()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // GetByIdAsync must throw, not return null
        var getThrew = false;
        try
        {
            await fake.GetByIdAsync(Guid.NewGuid(), Guid.NewGuid(), cts.Token);
        }
        catch (OperationCanceledException)
        {
            getThrew = true;
        }
        Assert.True(getThrew, "GetByIdAsync with cancelled token must throw OperationCanceledException, not return null.");

        // SaveAsync must throw, not return completed Task
        var saveThrew = false;
        try
        {
            await fake.SaveAsync(SyntheticProfileId, CreateSampleResponse(), cts.Token);
        }
        catch (OperationCanceledException)
        {
            saveThrew = true;
        }
        Assert.True(saveThrew, "SaveAsync with cancelled token must throw OperationCanceledException, not succeed silently.");
    }

    #endregion

    #region F. Confidentiality Tests

    [Fact]
    public void Interface_DoesNotExposePublicBulkListingMethod()
    {
        var interfaceMethods = typeof(IQuestionnairePersistenceRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance);

        Assert.DoesNotContain(interfaceMethods, m =>
            m.Name.StartsWith("GetAll", StringComparison.OrdinalIgnoreCase) ||
            m.Name.StartsWith("List", StringComparison.OrdinalIgnoreCase) ||
            m.Name.StartsWith("FindAll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RetrievedEntity_PreservesPrivateJsonSerializationBehavior()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var response = CreateSampleResponse(completed: true);
        await fake.SaveAsync(response.ProfileId, response);

        var retrieved = await fake.GetByIdAsync(response.ProfileId, response.Id);
        Assert.NotNull(retrieved);

        var json = JsonSerializer.Serialize(retrieved);

        Assert.Equal("{}", json);
        Assert.DoesNotContain(response.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(response.ProfileId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("v1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Answers", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetrievedEntity_DefaultJsonSerialization_DoesNotExposeRawAnswersOrIdentifiers()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var response = CreateSampleResponse(completed: true);
        await fake.SaveAsync(response.ProfileId, response);

        var retrieved = await fake.GetByIdAsync(response.ProfileId, response.Id);
        Assert.NotNull(retrieved);

        var json = JsonSerializer.Serialize(retrieved);

        for (var i = 1; i <= 12; i++)
        {
            Assert.DoesNotContain($"q{i:D2}", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task RetrievedEntity_ToString_ConcealsConfidentialDetails()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var response = CreateSampleResponse(completed: true);
        await fake.SaveAsync(response.ProfileId, response);

        var retrieved = await fake.GetByIdAsync(response.ProfileId, response.Id);
        Assert.NotNull(retrieved);

        var diagnosticString = retrieved.ToString();

        Assert.Equal("QuestionnaireResponseEntity [private]", diagnosticString);
        Assert.DoesNotContain(response.Id.ToString(), diagnosticString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(response.ProfileId.ToString(), diagnosticString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("q01", diagnosticString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InterfaceAssembly_DoesNotReferenceLearningOrHobbiesModules()
    {
        var assembly = typeof(IQuestionnairePersistenceRepository).Assembly;
        var referencedAssemblies = assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain("Andril.Learning", referencedAssemblies);
        Assert.DoesNotContain("Andril.Hobbies", referencedAssemblies);
    }

    #endregion

    #region G. Authorization Boundary Tests

    [Fact]
    public void MethodParameters_AcceptProfileAssociationIdentifiers_NotAuthenticationCredentials()
    {
        var getMethod = typeof(IQuestionnairePersistenceRepository).GetMethod("GetByIdAsync");
        Assert.NotNull(getMethod);
        var getParams = getMethod.GetParameters();
        Assert.Equal("profileId", getParams[0].Name);
        Assert.Equal(typeof(Guid), getParams[0].ParameterType);

        var saveMethod = typeof(IQuestionnairePersistenceRepository).GetMethod("SaveAsync");
        Assert.NotNull(saveMethod);
        var saveParams = saveMethod.GetParameters();
        Assert.Equal("profileId", saveParams[0].Name);
        Assert.Equal(typeof(Guid), saveParams[0].ParameterType);

        // Neither method accepts ClaimsPrincipal, tokens, or credential objects
        var prohibitedTypes = new[] { "ClaimsPrincipal", "AuthToken", "SecurityToken", "BearerToken", "Jwt", "Credential", "Session", "Password" };
        foreach (var p in getParams.Concat(saveParams))
        {
            if (p.ParameterType == typeof(CancellationToken))
            {
                continue;
            }

            foreach (var prohibited in prohibitedTypes)
            {
                Assert.DoesNotContain(prohibited, p.ParameterType.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(prohibited, p.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task GetByIdAsync_NullReturn_DenotesMissingRecordWithinAuthorizedScope()
    {
        var fake = new InMemoryQuestionnairePersistenceRepositoryFake();
        var authorizedProfileId = Guid.NewGuid();
        var nonExistentResponseId = Guid.NewGuid();

        // Null return represents an authorized lookup where the record does not exist
        var result = await fake.GetByIdAsync(authorizedProfileId, nonExistentResponseId);

        Assert.Null(result);
    }

    [Fact]
    public void Interface_DoesNotExposeAuthorizationResultOrPolicyTypes()
    {
        var methods = typeof(IQuestionnairePersistenceRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        foreach (var method in methods)
        {
            Assert.DoesNotContain("Authorization", method.ReturnType.Name, StringComparison.OrdinalIgnoreCase);
            foreach (var param in method.GetParameters())
            {
                Assert.DoesNotContain("Authorization", param.ParameterType.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Policy", param.ParameterType.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Interface_DoesNotExposeAuthenticationOrTokenValidationOperations()
    {
        var methods = typeof(IQuestionnairePersistenceRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        foreach (var method in methods)
        {
            Assert.DoesNotContain("Authenticate", method.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Authorize", method.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ValidateToken", method.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("VerifySession", method.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    #endregion

    #region Nested Test-Only Fake

    /// <summary>
    /// TEST-ONLY in-memory test double implementing <see cref="IQuestionnairePersistenceRepository"/>.
    /// <para>
    /// <b>Security and Scope Warning:</b> This fake exists strictly for testing repository contract semantics
    /// and demonstrates isolated, profile-scoped storage. It does NOT implement production authorization,
    /// transactions, encryption, database constraints, PostgreSQL schemas, concurrency control, or lifecycle rules.
    /// </para>
    /// </summary>
    private sealed class InMemoryQuestionnairePersistenceRepositoryFake : IQuestionnairePersistenceRepository
    {
        private readonly Dictionary<(Guid ProfileId, Guid ResponseId), QuestionnaireResponseEntity> _storage = new();

        public int StoredCount => _storage.Count;

        public Task<QuestionnaireResponseEntity?> GetByIdAsync(
            Guid profileId,
            Guid responseId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (profileId == Guid.Empty || responseId == Guid.Empty)
            {
                return Task.FromResult<QuestionnaireResponseEntity?>(null);
            }

            _storage.TryGetValue((profileId, responseId), out var entity);
            return Task.FromResult(entity);
        }

        public Task SaveAsync(
            Guid profileId,
            QuestionnaireResponseEntity response,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ArgumentNullException.ThrowIfNull(response);

            if (profileId == Guid.Empty)
            {
                throw new ArgumentException("Profile identifier cannot be empty.", nameof(profileId));
            }

            if (response.ProfileId != profileId)
            {
                throw new ArgumentException(
                    "Response profile identity does not match authorized profile scope.",
                    nameof(response));
            }

            _storage[(profileId, response.Id)] = response;
            return Task.CompletedTask;
        }
    }

    #endregion
}
