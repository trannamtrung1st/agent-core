using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Agents;

public static class SkillActivationLimits
{
    public const int MaxIdsPerLoad = 4;
    public const int MaxLoadInvocations = 2;
    public const int MaxAggregateProcedureCharacters = 8000;
}

public sealed record SkillLoadRejection(string Id, string Reason);

public sealed record SkillLoadPlan(
    IReadOnlyList<string> Admitted,
    IReadOnlyList<string> AlreadyActive,
    IReadOnlyList<SkillLoadRejection> Rejected,
    IReadOnlyList<string> IdsToAppend,
    bool IncrementInvocation,
    string Outcome)
{
    public string ToToolResultJson()
    {
        var rejected = Rejected.Select(item => new { id = item.Id, reason = item.Reason }).ToArray();
        return JsonSerializer.Serialize(new
        {
            admitted = Admitted,
            alreadyActive = AlreadyActive,
            rejected
        });
    }
}

public sealed record SkillLoadMailboxResult(
    string ToolResultJson,
    IReadOnlyList<string>? ActiveSkillKeys,
    string Outcome)
{
    public static SkillLoadMailboxResult Failed(string toolResultJson, string outcome) =>
        new(toolResultJson, null, outcome);
}

public static class SkillLoadAdmission
{
    public static bool TryParseIds(JsonElement args, out IReadOnlyList<string> ids, out string errorJson)
    {
        ids = [];
        if (args.ValueKind != JsonValueKind.Object)
        {
            errorJson = Error("invalid", "Tool arguments must be a JSON object.");
            return false;
        }

        var sawIds = false;
        foreach (var property in args.EnumerateObject())
        {
            if (!string.Equals(property.Name, "ids", StringComparison.Ordinal))
            {
                errorJson = Error("invalid", "Skill load accepts only ids.");
                return false;
            }

            sawIds = true;
        }

        if (!sawIds || args.GetProperty("ids").ValueKind != JsonValueKind.Array)
        {
            errorJson = Error("invalid", "Skill load requires an ids array.");
            return false;
        }

        var values = args.GetProperty("ids");
        if (values.GetArrayLength() is < 1 or > SkillActivationLimits.MaxIdsPerLoad)
        {
            errorJson = Error("invalid", "Skill load accepts 1 to 4 ids.");
            return false;
        }

        var parsed = new List<string>(values.GetArrayLength());
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                errorJson = Error("invalid", "Skill ids must be strings.");
                return false;
            }

            parsed.Add(value.GetString() ?? string.Empty);
        }

        ids = parsed;
        errorJson = string.Empty;
        return true;
    }

    public static SkillLoadPlan Plan(
        IReadOnlyList<EffectiveSkill> catalog,
        IReadOnlyList<string> pinnedIds,
        int skillLoadCount,
        IReadOnlyList<string> requestedIds)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(pinnedIds);
        ArgumentNullException.ThrowIfNull(requestedIds);
        if (skillLoadCount >= SkillActivationLimits.MaxLoadInvocations)
        {
            return new SkillLoadPlan(
                [],
                [],
                requestedIds.Select(id => new SkillLoadRejection(id.Trim(), "over_budget")).ToArray(),
                [],
                IncrementInvocation: false,
                Outcome: "over_budget");
        }

        var admitted = new List<string>();
        var alreadyActive = new List<string>();
        var rejected = new List<SkillLoadRejection>();
        var append = new List<string>();
        var working = new List<string>(pinnedIds);
        var characters = AggregateProcedureCharacters(catalog, working);
        var budgetClosed = false;
        foreach (var raw in requestedIds)
        {
            var id = raw.Trim();
            if (!ConversationTurnExecution.IsSkillKey(id))
            {
                rejected.Add(new SkillLoadRejection(id, "invalid"));
                continue;
            }

            var skill = catalog.FirstOrDefault(item => string.Equals(item.Key, id, StringComparison.Ordinal));
            if (skill is null)
            {
                rejected.Add(new SkillLoadRejection(id, "unknown"));
                continue;
            }

            if (working.Contains(id, StringComparer.Ordinal))
            {
                alreadyActive.Add(id);
                continue;
            }

            if (skill.Projection != SkillProjection.OnDemand) { rejected.Add(new(id, "not_on_demand")); continue; }

            if (budgetClosed
                || characters + skill.Procedure.Length > SkillActivationLimits.MaxAggregateProcedureCharacters)
            {
                budgetClosed = true;
                rejected.Add(new SkillLoadRejection(id, "over_budget"));
                continue;
            }

            working.Add(id);
            characters += skill.Procedure.Length;
            admitted.Add(id);
            append.Add(id);
        }

        return new SkillLoadPlan(
            admitted,
            alreadyActive,
            rejected,
            append,
            IncrementInvocation: true,
            Outcome: OutcomeOf(admitted, alreadyActive, rejected));
    }

    public static string Error(string code, string message) =>
        JsonSerializer.Serialize(new { error = code, message });

    private static int AggregateProcedureCharacters(IReadOnlyList<EffectiveSkill> catalog, IReadOnlyList<string> ids)
    {
        var total = 0;
        foreach (var id in ids)
        {
            var skill = catalog.FirstOrDefault(item => string.Equals(item.Key, id, StringComparison.Ordinal));
            if (skill is not null)
            {
                total += skill.Procedure.Length;
            }
        }

        return total;
    }

    private static string OutcomeOf(
        IReadOnlyList<string> admitted,
        IReadOnlyList<string> alreadyActive,
        IReadOnlyList<SkillLoadRejection> rejected)
    {
        if (admitted.Count > 0)
        {
            return "admitted";
        }

        if (rejected.Count == 0 && alreadyActive.Count > 0)
        {
            return "duplicate";
        }

        if (rejected.Count > 0 && rejected.All(item => item.Reason == "over_budget"))
        {
            return "over_budget";
        }

        if (rejected.Count > 0 && rejected.All(item => item.Reason is "unknown" or "invalid"))
        {
            return "unknown";
        }

        return "denied";
    }
}
