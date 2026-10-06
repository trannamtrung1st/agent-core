using AgentCore.Application.Sessions;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Continuity;

public sealed record ContinuityMaintenancePolicy(int PollIntervalSeconds = 60,
    int MinimumIntervalSeconds = 60, int DefaultIntervalSeconds = 300, int MaximumIntervalSeconds = 86400)
{
    public bool Valid => PollIntervalSeconds is >= 1 and <= 3600 && MinimumIntervalSeconds > 0
        && DefaultIntervalSeconds >= MinimumIntervalSeconds && MaximumIntervalSeconds >= DefaultIntervalSeconds;
    public bool Allows(int? seconds) => seconds is null || seconds >= MinimumIntervalSeconds && seconds <= MaximumIntervalSeconds;
    public int Effective(int? seconds) => seconds is { } value && Allows(value) ? value : DefaultIntervalSeconds;
    public void ValidateInterval(int? seconds)
    {
        if (!Allows(seconds)) throw AgentCoreErrors.Validation($"Continuity maintenance interval must be between {MinimumIntervalSeconds} and {MaximumIntervalSeconds} seconds, or null to use the system default.");
    }
}
