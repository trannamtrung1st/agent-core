using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed class SessionRuntimeFactory(
    ILanguageModelResolver models,
    IAgentBrain brain,
    IIdGenerator ids,
    TimeProvider time,
    Microsoft.Extensions.Logging.ILoggerFactory loggers,
    IInterruptionClassifier classifier,
    InteractionPolicy policy,
    ISpeechRecognizer? recognizer,
    ISpeechSynthesizer? synthesizer,
    VoiceAvailability voice,
    SessionToolExecutor tools,
    TenantRunContinuationService tenantContinuations,
    TenantArtifactReferenceService tenantArtifacts,
    TenantAttachmentRuntimeContextService tenantAttachments,
    TenantRunProfileService tenantProfiles,
    TenantMemoryService tenantMemory,
    TenantExperienceService tenantExperience,
    ILanguageModel? fixtureModel = null,
    IModelCatalog? fixtureCatalog = null,
    IDiagnosticIdSource? diagnostics = null)
{
    public SessionRuntime CreateTenant(SessionSnapshot snapshot, ISessionOutput output, TenantRuntimeFrame frame,
        ITenantRuntimeStore runtimeStorage, ITenantRunStore tenantRuns, AgentCore.Application.Security.TenantRunExecutionAuthority authority,
        AgentCore.Application.Security.TenantToolAuthorization tenantTools, TenantVoiceRuntimeBinding? tenantVoice = null)
    {
        if (snapshot.SessionId != frame.SessionId || snapshot.AgentInstanceId != frame.Owner.AgentInstanceId || snapshot.ProfileId is not null)
            throw new AgentCore.Application.Security.AccessDeniedException("tenant_runtime_scope_mismatch");
        return new(snapshot, fixtureModel, brain, new AgentCore.Application.Security.TenantRuntimeMemoryStore(runtimeStorage, frame, ids), output, ids, time,
            loggers.CreateLogger(typeof(SessionRuntime).FullName!), new AgentCore.Application.Security.TenantAgentRunStore(runtimeStorage, tenantRuns, ids, frame, authority),
            classifier, recognition: voice.EffectivePlan.RecognitionCapabilities ?? recognizer?.Capabilities,
            synthesizer: tenantVoice is null ? null : synthesizer, voice: tenantVoice is null ? null : voice,
            policy: policy, tools: new SessionToolExecutor(configurationGate: tools.ConfigurationGate, tenantAuthorization: tenantTools),
            modelResolver: models, catalog: fixtureCatalog, diagnostics: diagnostics, artifacts: tenantArtifacts.Bind(frame),
            runAuthority: authority, runtimeOwner: frame.Owner,
            tenantContinuations: tenantContinuations,
            tenantAttachments: tenantAttachments, tenantVoice: tenantVoice,
            tenantProfiles: tenantProfiles,
            tenantMemory: tenantMemory, tenantExperience: tenantExperience);
    }
    public ValueTask<ISpeechRecognitionSession> OpenTenantRecognitionAsync(RecognitionOptions options, CancellationToken ct)
        => (recognizer ?? throw AgentCoreErrors.VoiceUnavailable()).OpenAsync(options, ct);
}
