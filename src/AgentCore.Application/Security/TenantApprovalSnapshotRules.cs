using AgentCore.Domain.Definitions;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantApprovalSnapshotRules
{
    public static TenantApprovalSnapshot Persona(TenantAgent agent) => new(agent.Instance.Persona.Name, agent.Instance.Persona);
    public static TenantApprovalSnapshot Preferences(TenantAgent agent, AgentDefinition baseline) => new(agent.Instance.Persona.Name,
        PreferenceDefaults: new(baseline.ConversationPolicy.ResponseLength, baseline.ConversationPolicy.Language, baseline.ConversationPolicy.AskOneQuestionAtATime),
        PreferenceOverrides: Overrides(agent.Instance.SettingsOverrides));

    public static TenantApprovalPreferenceOverrides Overrides(InstanceSettingsOverrides? value) =>
        new(value?.ResponseLength?.Value, value?.Language?.Value, value?.AskOneQuestionAtATime?.Value);

    public static void Shape(TenantApprovalRequest request)
    {
        if (request.Snapshot is not { } s)
        {
            if (!request.Operation.IsLocalConfiguration) throw new ArgumentException("External operations require saved review evidence.");
            return; // Earlier local requests may lack display evidence; never invent it.
        }
        if (string.IsNullOrWhiteSpace(s.AgentName) || s.AgentName.Length > 256)
            throw new ArgumentException("Invalid approval display identity.");
        if (request.Operation.ReportSourceRead is not null || request.Operation.CompletionRead is not null || (request.Operation.BackgroundStart is not null || request.Operation.BackgroundReport is not null) || request.Operation.AttachmentRead is not null || request.Operation.WorkspaceMutation is not null || request.Operation.WorkspaceRead is not null || request.Operation.ArtifactPublication is not null || request.Operation.ArtifactVerification is not null)
        {
            if (s.BeforePersona is not null || s.PreferenceDefaults is not null || s.PreferenceOverrides is not null || s.Credential is not null
                || s.Sandbox is not null || s.SandboxControl is not null || s.SandboxRead is not null || s.Browser is not null || s.CredentialRead is not null)
                throw new ArgumentException("Unexpected Artifact publication review evidence.");
            return;
        }
        if (request.Operation.CredentialRead is { } credentialRead)
        {
            if (s.BeforePersona is not null || s.PreferenceDefaults is not null || s.PreferenceOverrides is not null || s.Credential is not null
                || s.Sandbox is not null || s.SandboxControl is not null || s.SandboxRead is not null || s.Browser is not null
                || s.CredentialRead?.Count != TenantApprovalRules.CredentialReadReferences(credentialRead).Count)
                throw new ArgumentException("Invalid Credential metadata review evidence.");
            return;
        }
        if (s.CredentialRead is not null) throw new ArgumentException("Unexpected Credential metadata review evidence.");
        if (request.Operation.BrowserCommand is { } browser)
        {
            var target = TenantApprovalRules.BrowserTarget(browser);
            if (s.BeforePersona is not null || s.PreferenceDefaults is not null || s.PreferenceOverrides is not null || s.Credential is not null
                || s.Sandbox is not null || s.SandboxControl is not null || s.SandboxRead is not null
                || s.Browser != new TenantApprovalBrowserSnapshot(browser.ToolName, target.Origin, target.ProfileMode.ToString()))
                throw new ArgumentException("Invalid Browser review snapshot.");
            return;
        }
        if (s.Browser is not null) throw new ArgumentException("Unexpected Browser review evidence.");
        if (request.Operation.Kind == TenantOperationKind.SandboxRead)
        {
            if (s.BeforePersona is not null || s.PreferenceDefaults is not null || s.PreferenceOverrides is not null || s.Credential is not null
                || s.Sandbox is not null || s.SandboxControl is not null || s.SandboxRead is not { } read) throw new ArgumentException("Invalid stored read review snapshot.");
            _ = TenantApprovalRules.ReadSnapshotLabels(request.Operation, read); return;
        }
        if (s.SandboxRead is not null) throw new ArgumentException("Unexpected stored read review evidence.");
        if (request.Operation.Kind == TenantOperationKind.SandboxControl)
        {
            if (s.BeforePersona is not null || s.PreferenceDefaults is not null || s.PreferenceOverrides is not null || s.Credential is not null || s.Sandbox is not null
                || s.SandboxControl is not { } control || string.IsNullOrWhiteSpace(control.ProfileName) || control.ProfileName.Length > 120
                || OciImageReference.Parse(control.ImageReference).Value != control.ImageReference
                || !Enum.TryParse<AgentCore.Domain.Conversation.SandboxLeaseState>(control.StoredState, out var lease) || !Enum.IsDefined(lease) || control.StoredState != lease.ToString()
                || control.ExecutionState is { } execution && (!Enum.TryParse<AgentCore.Domain.Conversation.SandboxExecutionState>(execution, out var e) || !Enum.IsDefined(e) || execution != e.ToString()))
                throw new ArgumentException("Invalid Sandbox control review snapshot.");
            return;
        }
        if (s.SandboxControl is not null) throw new ArgumentException("Unexpected Sandbox control evidence.");
        if (request.Operation.Kind == TenantOperationKind.SandboxCommand)
        {
            if (s.BeforePersona is not null || s.PreferenceDefaults is not null || s.PreferenceOverrides is not null || s.Credential is not null
                || s.Sandbox is not { } sandbox || string.IsNullOrWhiteSpace(sandbox.ProfileName) || sandbox.ProfileName.Length > 120
                || !double.IsFinite(sandbox.Cpu) || sandbox.Cpu is < .25 or > 2 || sandbox.MemoryMiB is < 256 or > 2048
                || sandbox.MaxCommandSeconds is < 1 or > 180 || sandbox.IdleTtlSeconds is < 60 or > 3600
                || sandbox.MaxLifetimeSeconds < sandbox.IdleTtlSeconds || sandbox.MaxLifetimeSeconds > 86400 || sandbox.StorageBytes is < 1 or > SandboxProfile.MaximumStorageBytes
                || OciImageReference.Parse(sandbox.ImageReference).Value != sandbox.ImageReference || !Script(sandbox.ProfileInitializationScript) || !Script(sandbox.InstanceInitializationScript))
                throw new ArgumentException("Invalid Sandbox command review snapshot.");
            return;
        }
        if (s.Sandbox is not null) throw new ArgumentException("Unexpected Sandbox review evidence.");
        if (request.Operation.Kind == TenantOperationKind.Persona)
        {
            if (s.BeforePersona is null || s.PreferenceDefaults is not null || s.PreferenceOverrides is not null || s.Credential is not null || s.BeforePersona.Name != s.AgentName)
                throw new ArgumentException("Invalid persona review snapshot.");
            AgentDefinitionValidator.ValidateIdentity(s.BeforePersona);
        }
        else if (request.Operation.Kind == TenantOperationKind.ConversationPreferences)
        {
            if (s.BeforePersona is not null || s.Credential is not null || s.PreferenceDefaults is not { } defaults || s.PreferenceOverrides is not { } overrides
                || defaults.ResponseLength is not ("concise" or "balanced") || !Language(defaults.Language)
                || overrides.ResponseLength is not (null or "concise" or "balanced") || overrides.Language is { } language && !Language(language))
                throw new ArgumentException("Invalid preference review snapshot.");
        }
        else if (s.BeforePersona is not null || s.PreferenceDefaults is not null || s.PreferenceOverrides is not null || s.Credential is not { } c
            || c.DisplayName != AgentCore.Domain.Credentials.CredentialRules.DisplayName(c.DisplayName)
            || c.Purpose is not ("EmailAccount" or "GoogleDriveAccount") || c.ConnectionStatus is not ("Connected" or "Configured"))
            throw new ArgumentException("Invalid credential check snapshot.");
        static bool Script(string? value)
        {
            try { return value is null || !value.Contains('\0') && new UTF8Encoding(false, true).GetByteCount(value) <= 16384; }
            catch (EncoderFallbackException) { return false; }
        }
        static bool Language(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && !value.Any(char.IsControl);
    }

    public static void RequireCurrent(TenantApprovalRequest request, TenantAgent agent)
    {
        Shape(request);
        if (request.Snapshot is not { } snapshot) return;
        if (snapshot.AgentName != agent.Instance.Persona.Name
            || request.Operation.Kind == TenantOperationKind.Persona && snapshot.BeforePersona != agent.Instance.Persona
            || request.Operation.Kind == TenantOperationKind.ConversationPreferences && snapshot.PreferenceOverrides != Overrides(agent.Instance.SettingsOverrides))
            throw new ArgumentException("Review snapshot does not match the witnessed target.");
    }
}
