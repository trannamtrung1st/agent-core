namespace AgentCore.Application.Ports;

public static class InitiativeIntents
{
    public const string Hint = "hint";
    public const string Rephrase = "rephrase";
    public const string Clarification = "clarification";
    public const string Reminder = "reminder";
    public const string FollowUp = "followUp";
    public const string Other = "other";

    public const int MaxPlannerNoteLength = 400;

    public static bool TryParse(string? value, out string intent)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            intent = Other;
            return false;
        }

        intent = value.Trim() switch
        {
            Hint or "Hint" => Hint,
            Rephrase or "Rephrase" => Rephrase,
            Clarification or "Clarification" => Clarification,
            Reminder or "Reminder" => Reminder,
            FollowUp or "follow_up" or "FollowUp" => FollowUp,
            Other or "Other" => Other,
            _ => Other
        };

        return intent == Other
            ? string.Equals(value.Trim(), Other, StringComparison.OrdinalIgnoreCase)
            : true;
    }
}

/// <summary>
/// Trusted <see cref="Intent"/> plus untrusted <see cref="PlannerNote"/> from the initiative evaluator.
/// Only <see cref="Intent"/> may shape fixed framework instructions; planner note is subordinate context.
/// </summary>
public sealed record InitiativePlan(string Intent, string PlannerNote);
