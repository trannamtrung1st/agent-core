using System.Text.Json;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Agents;

public static class ApplicationMessageLimits
{
    public const int MaxEffectKeyCharacters = 200;

    public static int MaxCharacters => ApplicationMessagePolicy.Default.MaxCharactersPerMessage;

    public static int MaxAdmittedPerExecution => ApplicationMessagePolicy.Default.MaxAdmittedPerExecution;

    public static int MaxAggregateCharactersPerExecution =>
        ApplicationMessagePolicy.Default.MaxAggregateCharactersPerExecution;
}

public sealed record ApplicationMessageMailboxResult(string ToolResultJson, string Outcome)
{
    public static ApplicationMessageMailboxResult Failed(string toolResultJson, string outcome) =>
        new(toolResultJson, outcome);
}

public static class ApplicationMessageAdmission
{
    public static bool TryCreateEffectKey(Guid executionId, string? toolCallId, out string effectKey)
    {
        effectKey = string.Empty;
        if (executionId == Guid.Empty || string.IsNullOrWhiteSpace(toolCallId))
        {
            return false;
        }

        var key = $"v1:{executionId:N}:{toolCallId}";
        if (key.Length > ApplicationMessageLimits.MaxEffectKeyCharacters)
        {
            return false;
        }

        effectKey = key;
        return true;
    }

    public static bool TryParseText(JsonElement args, out string text, out string errorJson)
    {
        text = string.Empty;
        if (args.ValueKind != JsonValueKind.Object)
        {
            errorJson = Error("invalid", "Tool arguments must be a JSON object.");
            return false;
        }

        var sawText = false;
        foreach (var property in args.EnumerateObject())
        {
            if (!string.Equals(property.Name, "text", StringComparison.Ordinal))
            {
                errorJson = Error("invalid", "Application messages accept only text.");
                return false;
            }

            sawText = true;
        }

        if (!sawText || args.GetProperty("text").ValueKind != JsonValueKind.String)
        {
            errorJson = Error("invalid", "Application messages require text.");
            return false;
        }

        var trimmed = args.GetProperty("text").GetString()?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            errorJson = Error("invalid", "Application message text is empty.");
            return false;
        }

        if (trimmed.Length > ApplicationMessageLimits.MaxCharacters)
        {
            errorJson = Error("invalid", "Application message text is too long.");
            return false;
        }

        text = trimmed;
        errorJson = string.Empty;
        return true;
    }

    public static string Success(string effectKey, ApplicationMessageBudget budgetAfter) =>
        JsonSerializer.Serialize(new
        {
            ok = true,
            effectId = effectKey,
            remainingMessages = budgetAfter.RemainingMessages,
            remainingCharacters = budgetAfter.RemainingCharacters
        });

    public static string Duplicate(string effectKey, ApplicationMessageBudget budget) =>
        JsonSerializer.Serialize(new
        {
            ok = true,
            duplicate = true,
            effectId = effectKey,
            remainingMessages = budget.RemainingMessages,
            remainingCharacters = budget.RemainingCharacters
        });

    public static string OverBudget(ApplicationMessageBudget budget) =>
        JsonSerializer.Serialize(new
        {
            error = "over_budget",
            message = "Intermediate message budget exhausted.",
            remainingMessages = budget.RemainingMessages,
            remainingCharacters = budget.RemainingCharacters
        });

    public static string Error(string code, string message) =>
        JsonSerializer.Serialize(new { error = code, message });
}
