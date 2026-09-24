namespace AgentCore.Application.Memory;

public enum ExplicitUserMemoryCaptureOutcome
{
    None = 0,
    Stored,
    Updated,
    AlreadyStored,
    StoredSessionOnly,
    UpdatedSessionOnly,
    Rejected,
    Unavailable
}

public static class ExplicitUserMemoryCapturePrompt
{
    public static string? Render(ExplicitUserMemoryCaptureOutcome outcome) => outcome switch
    {
        ExplicitUserMemoryCaptureOutcome.Stored =>
            "Explicit memory capture for the latest user request: Stored for cross-session recall.",
        ExplicitUserMemoryCaptureOutcome.Updated =>
            "Explicit memory capture for the latest user request: Updated for cross-session recall.",
        ExplicitUserMemoryCaptureOutcome.AlreadyStored =>
            "Explicit memory capture for the latest user request: AlreadyStored for cross-session recall.",
        ExplicitUserMemoryCaptureOutcome.StoredSessionOnly =>
            "Explicit memory capture for the latest user request: StoredSessionOnly. Cross-session memory was not updated; do not claim it will be remembered in a new session.",
        ExplicitUserMemoryCaptureOutcome.UpdatedSessionOnly =>
            "Explicit memory capture for the latest user request: UpdatedSessionOnly. Cross-session memory was not updated; do not claim it will be remembered in a new session.",
        ExplicitUserMemoryCaptureOutcome.Rejected =>
            "Explicit memory capture for the latest user request: Rejected. Do not claim the fact was remembered.",
        ExplicitUserMemoryCaptureOutcome.Unavailable =>
            "Explicit memory capture for the latest user request: Unavailable. Do not claim the fact was remembered.",
        _ => null
    };
}
