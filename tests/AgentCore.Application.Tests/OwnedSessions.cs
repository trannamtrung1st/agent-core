using System.Runtime.CompilerServices;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Experience;
using AgentCore.Application.Identity;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Speech;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Application.Tests;

/// <summary>Provisions actual instances for Session fixtures; production creation remains instance-only.</summary>
internal static class OwnedSessions
{
    private static readonly ConditionalWeakTable<SessionManager, IAgentInstanceService> Owners = new();
    public static SessionManager Manager(
        IAgentDefinitionStore definitions,
        IMemoryStore store,
        IIdGenerator ids,
        TimeProvider time,
        VoiceAvailability voice,
        IAttachmentStore? attachments = null,
        RoleKnowledgeService? knowledge = null,
        ISessionWorkspace? workspace = null,
        IArtifactStore? artifacts = null,
        IModelCatalog? models = null,
        ILocalUserProfileService? localProfiles = null,
        IStructuredMemoryStore? structuredMemory = null,
        IAgentInstanceService? instances = null,
        ITriggerPolicyRecoveryService? triggerPolicyRecovery = null,
        AdminLifecycleCoordinator? lifecycleGate = null,
        IBrowserLease? browserLease = null,
        ExperienceService? experience = null,
        AgentCore.Application.Workspaces.AgentInstanceWorkspaceService? agentWorkspace = null)
    {
        var instanceStore = new InMemoryAgentInstanceStore();
        instances ??= new AgentInstanceService(instanceStore, definitions, new SystemIdGenerator(time), time);
        if (workspace is not null && agentWorkspace is null)
            agentWorkspace = new AgentCore.Application.Workspaces.AgentInstanceWorkspaceService(instanceStore,
                new AgentCore.Infrastructure.Workspaces.FileAgentInstanceWorkspaceStore(
                    Path.Combine(Path.GetTempPath(), "session-fixture-home-" + Guid.NewGuid().ToString("N")), time, new SystemIdGenerator(time)),
                store, workspace, lifecycleGate ?? new AdminLifecycleCoordinator());
        var manager = new SessionManager(definitions, store, ids, time, voice,
            attachments, knowledge, workspace, artifacts, models, localProfiles, structuredMemory,
            instances, triggerPolicyRecovery, lifecycleGate, browserLease, experience, agentWorkspace);
        Owners.Add(manager, instances);
        return manager;
    }

    public static async Task<SessionSnapshot> CreateOwnedAsync(this SessionManager manager,
        string agentId, int? agentVersion, SessionMode mode, CancellationToken cancellationToken = default,
        SessionPurpose? purpose = null, SessionCompletionPolicy? policy = null, TimeSpan? maxDuration = null,
        string? speechLocaleOverride = null, string? modelKey = null, string? reasoningEffort = null,
        ModelSelectionSource modelSource = ModelSelectionSource.SystemDefault)
    {
        var instance = await Owners.GetValue(manager, _ => throw new InvalidOperationException("Use OwnedSessions.Manager to wire instance fixtures."))
            .CreateAsync(agentId, agentVersion, cancellationToken);
        return await manager.CreateForInstanceAsync(instance.InstanceId, mode, cancellationToken,
            purpose, policy, maxDuration, speechLocaleOverride, modelKey, reasoningEffort, modelSource);
    }
}
