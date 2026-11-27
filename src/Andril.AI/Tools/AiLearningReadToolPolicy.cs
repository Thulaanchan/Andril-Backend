using System.Text.Json;
using System.Text.Json.Serialization;
using Andril.AI.ChatBox;
using Andril.AI.DTOs;

namespace Andril.AI.Tools;

public enum AiLearningReadTool { ClassroomSearch = 1, LearningPartnerDiscovery = 2 }

/// <summary>AI-owned provisional input policy only. A parsed query is untrusted data, not
/// authority, eligibility evidence, a dispatch permit or a tool result. No production tool
/// reader/schema is defined here: M3/M5 output/eligibility contracts and fresh M2 coordination
/// remain required. Never infer a write, message, join, XP, purchase or private-data capability.</summary>
public sealed class AiLearningReadToolQuery
{
    public AiLearningReadTool Tool { get; }
    [JsonIgnore] public string Query { get; }
    private AiLearningReadToolQuery(AiLearningReadTool tool, string query) { Tool = tool; Query = query; }

    public static bool TryParse(AiModule module, string? toolName, string? arguments, out AiLearningReadToolQuery? result)
    {
        result = null;
        if (module != AiModule.Learning || string.IsNullOrWhiteSpace(arguments)) return false;
        var tool = toolName switch
        {
            "search_classrooms" => AiLearningReadTool.ClassroomSearch,
            "discover_learning_partners" => AiLearningReadTool.LearningPartnerDiscovery,
            _ => (AiLearningReadTool)0
        };
        if (tool == 0) return false;
        try
        {
            using var json = JsonDocument.Parse(arguments);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return false;
            var fields = json.RootElement.EnumerateObject().ToArray();
            // Exact cardinality also rejects duplicate keys and all actor/authorization fields.
            if (fields.Length != 1 || fields[0].Name != "query" || fields[0].Value.ValueKind != JsonValueKind.String) return false;
            var query = fields[0].Value.GetString();
            if (!AiPublicContractValidation.ValidInput(query)) return false;
            result = new(tool, query!); return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException) { return false; }
    }
    public override string ToString() => "Untrusted read-only Learning query [private text omitted; execution not authorized]";
}
