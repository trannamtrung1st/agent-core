namespace AgentCore.Domain.Definitions;

public static class SkillCapabilities { public const string ChatRespond = "chat.respond"; }
[System.Text.Json.Serialization.JsonConverter(typeof(SkillProjectionJsonConverter))]
public enum SkillProjection { Always, OnDemand }
public sealed class SkillProjectionJsonConverter : System.Text.Json.Serialization.JsonConverter<SkillProjection>
{
    public override SkillProjection Read(ref System.Text.Json.Utf8JsonReader reader, Type type, System.Text.Json.JsonSerializerOptions options) =>
        reader.TokenType == System.Text.Json.JsonTokenType.String ? reader.GetString() switch {
            "Always" => SkillProjection.Always, "OnDemand" => SkillProjection.OnDemand,
            _ => throw new System.Text.Json.JsonException("Skill projection must be Always or OnDemand.")
        } : throw new System.Text.Json.JsonException("Skill projection must be a string.");
    public override void Write(System.Text.Json.Utf8JsonWriter writer, SkillProjection value, System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch { SkillProjection.Always => "Always", SkillProjection.OnDemand => "OnDemand",
            _ => throw new System.Text.Json.JsonException("Skill projection is invalid.") });
}
public enum SkillOrigin { Definition, Instance }
public enum SkillAuthor { Admin, Agent }

public sealed record SkillSpec(
    string Id, string Name, string Description, string Procedure,
    [property: System.Text.Json.Serialization.JsonRequired] SkillProjection Projection, [property: System.Text.Json.Serialization.JsonRequired] bool DefaultEnabled,
    IReadOnlyList<string> RequiredCapabilities, IReadOnlyList<string> ResourcePaths);

public sealed record AgentDefinitionSkillState(Guid AgentInstanceId, string DefinitionSkillId,
    bool Enabled, long Revision, DateTimeOffset UpdatedAt);

public sealed record AgentInstanceSkill(string SkillId, Guid AgentInstanceId,
    string Name, string Description, string Procedure, SkillProjection Projection, bool Enabled,
    IReadOnlyList<string> RequiredCapabilities, long Revision, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, SkillAuthor CreatedBy, string? SourceDefinitionId = null,
    int? SourceDefinitionVersion = null, string? SourceDefinitionSkillId = null);

public sealed record EffectiveSkill(string Key, SkillOrigin Origin, string SourceId,
    string Name, string Description, string Procedure, SkillProjection Projection,
    IReadOnlyList<string> RequiredCapabilities, IReadOnlyList<string> ResourcePaths);

public static class SkillKeys
{
    public static bool IsValid(string? key) => key is not null &&
        (key.StartsWith("definition:", StringComparison.Ordinal) && SkillIds.IsValid(key[11..])
        || key.StartsWith("instance:", StringComparison.Ordinal) && (SkillIds.IsValid(key[9..]) || Guid.TryParseExact(key[9..], "D", out var id) && id != Guid.Empty));
}

public static class SkillPolicy
{
    // Existing 8,000-character active procedure budget protects prompt context, regardless of count.
    public const int MaxActiveProcedureCharacters = 8000;
    public static IReadOnlyList<EffectiveSkill> FreezeCatalog(IReadOnlyList<EffectiveSkill> catalog)
    {
        if (catalog.Any(s => !SkillKeys.IsValid(s.Key) || !Enum.IsDefined(s.Origin)
            || s.Key != (s.Origin == SkillOrigin.Definition ? "definition:" : "instance:") + s.SourceId
            || s.Origin == SkillOrigin.Instance && s.ResourcePaths.Count != 0)
            || catalog.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count() != catalog.Count)
            throw new ArgumentException("Pinned Skill catalog keys are invalid.");
        foreach (var skill in catalog) Validate(skill.Name, skill.Description, skill.Procedure, skill.Projection, skill.RequiredCapabilities);
        return Array.AsReadOnly(catalog.Select(s => s with {
            RequiredCapabilities = Array.AsReadOnly(s.RequiredCapabilities.ToArray()),
            ResourcePaths = Array.AsReadOnly(s.ResourcePaths.ToArray()) }).ToArray());
    }
    public static void ValidateAlwaysBudget(IEnumerable<(SkillProjection Projection, bool Enabled, string Procedure)> skills)
    {
        if (skills.Where(s => s.Enabled && s.Projection == SkillProjection.Always).Sum(s => s.Procedure.Length) > MaxActiveProcedureCharacters)
            throw new ArgumentException("Enabled Always Skills exceed the 8000-character procedure context budget.");
    }
    public static void ValidateActive(IReadOnlyList<EffectiveSkill> catalog, IReadOnlyList<string> keys)
    {
        if (keys.Any(k => !catalog.Any(s => s.Key == k))
            || catalog.Where(s => keys.Contains(s.Key, StringComparer.Ordinal)).Sum(s => s.Procedure.Length) > MaxActiveProcedureCharacters)
            throw new ArgumentException("Active Skills must fit the procedure budget and belong to the pinned catalog.");
    }
    public static void Validate(string name, string description, string procedure, SkillProjection projection,
        IReadOnlyList<string> capabilities)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new ArgumentException("Skill name must contain 1 to 80 characters.");
        if (string.IsNullOrWhiteSpace(description) || description.Length > 240) throw new ArgumentException("Skill description must contain 1 to 240 characters.");
        if (string.IsNullOrWhiteSpace(procedure) || procedure.Length > 4000) throw new ArgumentException("Skill procedure must contain 1 to 4000 characters.");
        if (!Enum.IsDefined(projection)) throw new ArgumentException("Skill projection must be Always or OnDemand.");
        if (capabilities is null || capabilities.Count > 8 || capabilities.Distinct(StringComparer.Ordinal).Count() != capabilities.Count
            || capabilities.Any(c => !SkillIds.IsValid(c))) throw new ArgumentException("Skill required capabilities are invalid.");
    }
}
