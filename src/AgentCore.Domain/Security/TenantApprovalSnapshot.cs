using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Security;

// Display evidence captured by Core; never parameters, permission or a grant supplied by a caller.
public sealed record TenantApprovalPreferenceValues(string ResponseLength, string Language, bool AskOneQuestionAtATime);
public sealed record TenantApprovalPreferenceOverrides(string? ResponseLength, string? Language, bool? AskOneQuestionAtATime);
public sealed record TenantApprovalCredentialSnapshot(string DisplayName, string Purpose, string ConnectionStatus);
public sealed record TenantApprovalSandboxSnapshot(string ProfileName, string ImageReference,
    string? ProfileInitializationScript, string? InstanceInitializationScript, bool WillInitialize, double Cpu, int MemoryMiB,
    int MaxCommandSeconds, int IdleTtlSeconds, int MaxLifetimeSeconds, long StorageBytes);
public sealed record TenantApprovalSnapshot(string AgentName, AgentIdentity? BeforePersona = null,
    TenantApprovalPreferenceValues? PreferenceDefaults = null, TenantApprovalPreferenceOverrides? PreferenceOverrides = null,
    TenantApprovalCredentialSnapshot? Credential = null, TenantApprovalSandboxSnapshot? Sandbox = null);
