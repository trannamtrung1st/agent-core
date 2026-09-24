namespace AgentCore.Application.Memory;

public enum ExplicitUserMemoryCaptureOutcome
{
    None = 0,
    Stored,
    Updated,
    AlreadyStored,
    Rejected,
    Unavailable
}

public static class ExplicitUserMemoryCapturePrompt
{
    public static string? Render(ExplicitUserMemoryCaptureOutcome outcome) => outcome switch
    {
        ExplicitUserMemoryCaptureOutcome.Stored =>
            "Explicit memory capture for the latest user request: Stored.",
        ExplicitUserMemoryCaptureOutcome.Updated =>
            "Explicit memory capture for the latest user request: Updated.",
        ExplicitUserMemoryCaptureOutcome.AlreadyStored =>
            "Explicit memory capture for the latest user request: AlreadyStored.",
        ExplicitUserMemoryCaptureOutcome.Rejected =>
            "Explicit memory capture for the latest user request: Rejected. Do not claim the fact was remembered.",
        ExplicitUserMemoryCaptureOutcome.Unavailable =>
            "Explicit memory capture for the latest user request: Unavailable. Do not claim the fact was remembered.",
        _ => null
    };
}
