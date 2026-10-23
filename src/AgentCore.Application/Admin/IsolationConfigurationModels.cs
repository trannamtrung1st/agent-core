using System.Text.Json;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Admin;

public sealed record IsolationSectionPatch(string Section, IReadOnlyDictionary<string, JsonElement> Set, IReadOnlyList<string> Clear);
public sealed record IsolationDraft(IReadOnlyList<IsolationSectionPatch> Settings, bool ExperienceEnabled, bool AllowMaintenance);
public sealed record IsolationChange(string Owner, string Field, JsonElement Before, JsonElement After, string Source);
public sealed record IsolationConfigurationView(IsolationOwnerRevisions Revisions, IReadOnlyList<InstanceSettingsSection> Settings,
    bool ExperienceEnabled, bool AllowMaintenance, string HostBrowserMode, bool BrowserEnabled, string SandboxEnforcementState = "Enforced");
public sealed record IsolationChangePlan(IsolationOwnerRevisions Expected, IsolationDraft Draft, string Fingerprint,
    IReadOnlyList<IsolationChange> Changes, IReadOnlyList<string> BlockingReasons, string Notice);
