using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Conversation;

/// <summary>One internally consistent generation. Resources carry immutable hashes, never binary bytes.</summary>
public sealed class AgentRunConfiguration
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public AgentRunConfiguration(AgentDefinition definition, long instanceRevision, long personaRevision,
        IReadOnlyList<EffectiveAgentResource> resources, IReadOnlyList<AgentCore.Domain.Credentials.AgentCredentialBinding>? credentialBindings = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(resources);
        if (instanceRevision < 0 || personaRevision < 0) throw new ArgumentException("Configuration revisions cannot be negative.");
        if (resources.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() != resources.Count
            || resources.Any(r => r.Key is null || !(r.Key.StartsWith("definition:", StringComparison.Ordinal) || r.Key.StartsWith("instance:", StringComparison.Ordinal))
                || r.ContentSha256.Length != 64 || r.ContentSha256.Any(c => !char.IsAsciiHexDigit(c)) || r.ByteLength <= 0))
            throw new ArgumentException("Resource manifest identity/content is invalid.");
        Resources = Array.AsReadOnly(resources.ToArray());
        CredentialBindings = Array.AsReadOnly((credentialBindings ?? []).ToArray());
        InstanceRevision = instanceRevision; PersonaRevision = personaRevision;
        Definition = Freeze(definition) with { ExecutionResources = Resources };
        ConfigurationHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { Definition, InstanceRevision, PersonaRevision, Resources, CredentialBindings }, Json))).ToLowerInvariant();
    }
    public AgentDefinition Definition { get; }
    public long InstanceRevision { get; }
    public long PersonaRevision { get; }
    public IReadOnlyList<EffectiveAgentResource> Resources { get; }
    public string ConfigurationHash { get; }
    public IReadOnlyList<AgentCore.Domain.Credentials.AgentCredentialBinding> CredentialBindings { get; }

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> values) => Array.AsReadOnly(values.ToArray());
    private static AgentDefinition Freeze(AgentDefinition d) => d with {
        ExecutionSandboxProfiles = d.ExecutionSandboxProfiles is { } profiles ? Copy(profiles) : null,
        SandboxPolicy = d.SandboxPolicy is { } sandbox ? sandbox with { AllowedProfileIds = Copy(sandbox.AllowedProfileIds) } : null,
        SelfManagement = d.SelfManagement is { } management ? management with { Scopes = Copy(management.Scopes), Sources = Copy(management.Sources), EligibleTools = Copy(management.EligibleTools), ManagedAutoApply = Copy(management.ManagedAutoApply ?? []) } : null,
        Goals = Copy(d.Goals), Metadata = new ReadOnlyDictionary<string, string>(d.Metadata.ToDictionary(p => p.Key, p => p.Value)),
        InitiativePolicy = d.InitiativePolicy with { Triggers = Copy(d.InitiativePolicy.Triggers) },
        TriggerPolicy = d.TriggerPolicy is { } trigger ? trigger with { AllowedSourceKinds = Copy(trigger.AllowedSourceKinds) } : null,
        Skills = Copy(d.SkillList.Select(s => s with { RequiredCapabilities = Copy(s.RequiredCapabilities), ResourcePaths = Copy(s.ResourcePaths) }).ToArray()),
        Environment = d.Environment is { } env ? env with {
            Harness = Copy(env.HarnessList), KnowledgeSources = Copy(env.KnowledgeList), ToolAllowlist = env.ToolAllowlist is null ? null : Copy(env.ToolAllowlist),
            Capabilities = env.Capabilities is { } caps ? caps with { ResolvedCapabilities = Copy(caps.ResolvedCapabilities) } : null,
            Projection = env.Projection is { } projection ? projection with { AlwaysCapabilities = Copy(projection.AlwaysCapabilities) } : null
        } : null
    };
}
