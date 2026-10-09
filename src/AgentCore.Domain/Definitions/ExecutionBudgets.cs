namespace AgentCore.Domain.Definitions;

public enum ExecutionBudgetClass { Standard, InteractiveBrowser, UnattendedBoundBrowser }
public enum ExecutionBudgetPreset { Standard, Extended, DeepWorkflow, Custom }

/// <summary>Resource policy only; never an authorization grant.</summary>
public sealed record ExecutionBudgetProfile(int MaxSteps, int DurationSeconds, int PerToolSeconds = 30,
    ExecutionBudgetPreset Preset = ExecutionBudgetPreset.Custom)
{
    public static ExecutionBudgetProfile For(ExecutionBudgetClass kind, ExecutionBudgetPreset preset)
    {
        if (preset == ExecutionBudgetPreset.Custom || !Enum.IsDefined(preset) || !Enum.IsDefined(kind))
            throw new ArgumentException("Select a known preset or supply bounded custom values.");
        var factor = (int)preset + 1;
        var (steps, seconds) = kind switch
        {
            ExecutionBudgetClass.InteractiveBrowser => (48, 300),
            ExecutionBudgetClass.UnattendedBoundBrowser => (32, 240),
            _ => (24, 180)
        };
        return new(steps * factor, seconds * factor, 30, preset);
    }
    public void Validate(ExecutionBudgetCeilings? ceilings = null)
    {
        ceilings ??= ExecutionBudgetCeilings.Default;
        ceilings.Validate();
        if (!Enum.IsDefined(Preset) || MaxSteps < 8 || MaxSteps > ceilings.MaxSteps
            || DurationSeconds < 60 || DurationSeconds > ceilings.DurationSeconds
            || PerToolSeconds < 1 || PerToolSeconds > ceilings.PerToolSeconds || PerToolSeconds > DurationSeconds)
            throw new ArgumentException($"Execution budget requires 8–{ceilings.MaxSteps} steps, 60–{ceilings.DurationSeconds} seconds and 1–{ceilings.PerToolSeconds} seconds per tool.");
    }
}

public sealed record ExecutionBudgetCeilings(int MaxSteps = 144, int DurationSeconds = 900, int PerToolSeconds = 30)
{
    public static ExecutionBudgetCeilings Default { get; } = new();
    public void Validate()
    {
        if (MaxSteps is < 8 or > 144 || DurationSeconds is < 60 or > 900 || PerToolSeconds is < 1 or > 30)
            throw new ArgumentException("Host execution ceilings must stay within 144 steps, 900 seconds and 30 seconds per tool.");
    }
}

/// <summary>Null class entries inherit independently.</summary>
public sealed record ExecutionBudgetPolicy(ExecutionBudgetProfile? Standard = null,
    ExecutionBudgetProfile? InteractiveBrowser = null, ExecutionBudgetProfile? UnattendedBoundBrowser = null)
{
    public ExecutionBudgetProfile? Get(ExecutionBudgetClass kind) => kind switch
    {
        ExecutionBudgetClass.Standard => Standard,
        ExecutionBudgetClass.InteractiveBrowser => InteractiveBrowser,
        ExecutionBudgetClass.UnattendedBoundBrowser => UnattendedBoundBrowser,
        _ => throw new ArgumentException("Unknown execution class.")
    };
    public void Validate(ExecutionBudgetCeilings? ceilings = null)
    {
        foreach (var kind in Enum.GetValues<ExecutionBudgetClass>())
        {
            var profile = Get(kind); profile?.Validate(ceilings);
            if (profile is not null && profile.Preset != ExecutionBudgetPreset.Custom && profile != ExecutionBudgetProfile.For(kind, profile.Preset))
                throw new ArgumentException("Preset limits must match the selected execution class. Select Custom to edit numeric limits.");
        }
    }
    public static EffectiveExecutionBudget Resolve(ExecutionBudgetClass kind, ExecutionBudgetPolicy? definition,
        ExecutionBudgetPolicy? instance, ExecutionBudgetCeilings? ceilings = null)
    {
        var overridden = instance?.Get(kind);
        var inherited = definition?.Get(kind);
        var profile = overridden ?? inherited ?? ExecutionBudgetProfile.For(kind, ExecutionBudgetPreset.Standard);
        profile.Validate(ceilings);
        return new(kind, profile, overridden is not null ? "instance" : inherited is not null ? "definition" : "system");
    }
}

public sealed record BrowserCleanupIntent(bool LogoutRequested, bool ClosureRequested)
{
    public bool Any => LogoutRequested || ClosureRequested;
}

public sealed record EffectiveExecutionBudget(ExecutionBudgetClass Class, ExecutionBudgetProfile Profile, string Source,
    bool RequestedCleanup = false, BrowserCleanupIntent? CleanupIntent = null);
