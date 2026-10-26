using System.Text.Json.Serialization;

namespace AgentCore.Domain.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReasoningPreference([property: JsonRequired] string Mode, string? Effort = null)
{
    public static ReasoningPreference Default { get; } = new("modelDefault");
    public void Validate()
    {
        if (Mode is not ("modelDefault" or "fixed" or "auto") || (Mode == "fixed") != (Effort is not null)
            || Effort is { } effort && (effort.Length is < 1 or > 32 || effort.Any(char.IsControl)))
            throw new ArgumentException("Use modelDefault, auto, or fixed reasoning with one supported effort.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelPreference([property: JsonRequired] string Mode, string? OfferingId = null, ReasoningPreference? Reasoning = null)
{
    public static ModelPreference Inherit { get; } = new("inherit");
    public static ModelPreference Auto { get; } = new("auto", Reasoning: new("auto"));
    public static ModelPreference Fixed(string id, string? effort = null) => new("fixed", id,
        effort is null ? ReasoningPreference.Default : new("fixed", effort));
    public void Validate()
    {
        if (Mode is not ("inherit" or "fixed" or "auto") || (Mode == "fixed") != (OfferingId is not null)
            || OfferingId is { } id && (!Guid.TryParseExact(id, "D", out var parsed) || parsed == Guid.Empty || parsed.ToString("D") != id))
            throw new ArgumentException("Use inherit, auto, or fixed with a canonical offering UUID.");
        Reasoning?.Validate();
    }
}

public sealed record ModelDecisionEvidence(string Basis, string EngineId, string EngineVersion, long SettingsRevision,
    string PreferenceSource, string PolicyRevision, string? DiagnosticCode = null);

public sealed record AutoRoutingPolicy(bool Enabled = false, bool AllowConfiguredDefault = false, double MinimumProbability = 0.7)
{
    public void Validate()
    {
        if (!double.IsFinite(MinimumProbability) || MinimumProbability is < 0.5 or > 1)
            throw new ArgumentException("Routing minimum probability must be finite and between 0.5 and 1.");
    }
}
