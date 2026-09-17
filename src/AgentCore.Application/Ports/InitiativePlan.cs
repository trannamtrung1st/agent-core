namespace AgentCore.Application.Ports;

public enum InitiativeIntent
{
    Hint,
    Rephrase,
    Clarification,
    Reminder,
    FollowUp,
    Other
}

public static class InitiativeIntents
{
    public const int MaxPlannerNoteLength = 400;

    public static string ToWire(InitiativeIntent intent) =>
        intent switch
        {
            InitiativeIntent.Hint => "hint",
            InitiativeIntent.Rephrase => "rephrase",
            InitiativeIntent.Clarification => "clarification",
            InitiativeIntent.Reminder => "reminder",
            InitiativeIntent.FollowUp => "followUp",
            _ => "other"
        };

    public static bool TryParse(string? value, out InitiativeIntent intent)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            intent = InitiativeIntent.Other;
            return false;
        }

        intent = value.Trim() switch
        {
            "hint" or "Hint" => InitiativeIntent.Hint,
            "rephrase" or "Rephrase" => InitiativeIntent.Rephrase,
            "clarification" or "Clarification" => InitiativeIntent.Clarification,
            "reminder" or "Reminder" => InitiativeIntent.Reminder,
            "followUp" or "follow_up" or "FollowUp" => InitiativeIntent.FollowUp,
            "other" or "Other" => InitiativeIntent.Other,
            _ => InitiativeIntent.Other
        };

        return intent == InitiativeIntent.Other
            ? string.Equals(value.Trim(), "other", StringComparison.OrdinalIgnoreCase)
            : true;
    }

    public static bool TryNormalizePlannerNote(string? note, out string normalized)
    {
        normalized = note?.Trim() ?? string.Empty;
        return normalized.Length > 0 && normalized.Length <= MaxPlannerNoteLength;
    }

    public static string ClipPlannerNote(string note)
    {
        var trimmed = note.Trim();
        if (trimmed.Length <= MaxPlannerNoteLength)
        {
            return trimmed;
        }

        return trimmed[..MaxPlannerNoteLength];
    }
}

/// <summary>
/// Trusted <see cref="Intent"/> plus untrusted <see cref="PlannerNote"/> from the initiative evaluator.
/// Only <see cref="Intent"/> may shape fixed framework instructions; planner note is subordinate context.
/// </summary>
public sealed record InitiativePlan
{
    public InitiativeIntent Intent { get; }
    public string PlannerNote { get; }

    private InitiativePlan(InitiativeIntent intent, string plannerNote)
    {
        Intent = intent;
        PlannerNote = plannerNote;
    }

    public static bool TryCreate(string? intentWire, string? plannerNote, out InitiativePlan? plan)
    {
        plan = null;
        if (!InitiativeIntents.TryParse(intentWire, out var intent))
        {
            return false;
        }

        if (!InitiativeIntents.TryNormalizePlannerNote(plannerNote, out var normalized))
        {
            return false;
        }

        plan = new InitiativePlan(intent, normalized);
        return true;
    }

    public static InitiativePlan Create(InitiativeIntent intent, string plannerNote)
    {
        if (!InitiativeIntents.TryNormalizePlannerNote(plannerNote, out var normalized))
        {
            throw new ArgumentException(
                "Planner note must be non-empty and at most " + InitiativeIntents.MaxPlannerNoteLength + " characters.",
                nameof(plannerNote));
        }

        return new InitiativePlan(intent, normalized);
    }
}
