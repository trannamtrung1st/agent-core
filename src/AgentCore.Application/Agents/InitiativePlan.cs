namespace AgentCore.Application.Agents;

public static class InitiativeIntents
{
    public const string Hint = "hint";
    public const string Rephrase = "rephrase";
    public const string Clarification = "clarification";
    public const string Reminder = "reminder";
    public const string FollowUp = "followUp";
    public const string Other = "other";

    public static string Normalize(string? intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
        {
            return Other;
        }

        var normalized = intent.Trim();
        return normalized switch
        {
            Hint or "Hint" => Hint,
            Rephrase or "Rephrase" => Rephrase,
            Clarification or "Clarification" => Clarification,
            Reminder or "Reminder" => Reminder,
            FollowUp or "follow_up" or "FollowUp" => FollowUp,
            _ => Other
        };
    }
}

public sealed record InitiativePlan(string Intent, string Objective);
