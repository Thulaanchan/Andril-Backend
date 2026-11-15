using System.Text.Json;
using System.Text.Json.Serialization;
using Andril.Common.Accounts.StarterModules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Andril.API.Controllers.Common;

[ApiController]
[Authorize]
[Route("api/v1/account/starter-module")]
public sealed class StarterModuleController(IStarterModuleService starter) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<StarterModuleStatus>> Get(CancellationToken cancellationToken) =>
        Ok(await starter.GetAsync(cancellationToken));

    [HttpPost]
    public async Task<ActionResult<StarterModuleStatus>> Post([FromBody] StarterModuleRequest request, CancellationToken cancellationToken) =>
        Ok(await starter.SelectAsync(request.Module, request.SocialOptIn, cancellationToken));
}

[JsonConverter(typeof(StarterModuleRequestConverter))]
public sealed class StarterModuleRequest
{
    public string? Module { get; init; }
    public bool? SocialOptIn { get; init; }
}

// Scope duplicate-property rejection to this feature; do not change other APIs' JSON options.
public sealed class StarterModuleRequestConverter : JsonConverter<StarterModuleRequest>
{
    public override StarterModuleRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("A single starter selection is required.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name)) throw new JsonException("Duplicate selection fields are not allowed.");
        var payload = document.RootElement.Deserialize<Payload>(options) ?? throw new JsonException("A selection is required.");
        return new() { Module = payload.Module, SocialOptIn = payload.SocialOptIn };
    }
    public override void Write(Utf8JsonWriter writer, StarterModuleRequest value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new Payload { Module = value.Module, SocialOptIn = value.SocialOptIn }, options);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed class Payload
    {
        public string? Module { get; init; }
        public bool? SocialOptIn { get; init; }
    }
}
