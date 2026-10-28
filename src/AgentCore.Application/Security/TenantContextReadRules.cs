using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using Variable = AgentCore.Domain.Spaces.EnvironmentVariable;

namespace AgentCore.Application.Security;

// Same-transaction source fence for persisted variable observations, including final output.
public static class TenantContextReadRules
{
    public static async ValueTask RequireAsync(AgentRun run, TenantSecurityState state, TenantAgent agent, AgentDefinition baseline, IamAuthorizer iam,
        Func<string, ValueTask<Variable?>> variable, Func<ValueTask<IReadOnlyList<TenantCredentialBinding>>> credentials,
        Func<Guid, ValueTask<bool>> secretAvailable, Func<ValueTask<long?>> environmentRevision, CancellationToken ct)
    {
        if (!AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return;
        foreach (var message in messages!.Where(m => m.Role == ModelRole.Tool && m.Name is "variables.list" or "variables.resolve"))
        {
            using var json = JsonDocument.Parse(message.Text); var body = json.RootElement;
            if (!body.TryGetProperty("contextRead", out var witness)) continue;
            var pin = run.Admission.Configuration!; var location = pin.SpaceContext!;
            if (!location.EnvironmentsEnabled || location.EnvironmentId is null || witness.GetProperty("tool").GetString() != message.Name
                || witness.GetProperty("spaceId").GetGuid() != location.SpaceId || witness.GetProperty("environmentId").GetGuid() != location.EnvironmentId
                || !TenantContextToolContract.Allows(pin.Definition, message.Name!) || !TenantContextToolContract.Allows(InstanceSettingsResolver.ResolveForAgent(baseline, agent), message.Name!))
                throw new AccessDeniedException("context_source_changed");
            var scope = TenantSpaceService.Scope(state.Tenant.TenantId, location.SpaceId);
            await Permission("environments.read", scope).ConfigureAwait(false);
            await Permission(message.Name == "variables.list" ? "variables.read" : "variables.resolve", scope).ConfigureAwait(false);
            if (!witness.TryGetProperty("environmentRevision", out var profileRevision) || profileRevision.ValueKind != JsonValueKind.Number
                || await environmentRevision().ConfigureAwait(false) != profileRevision.GetInt64()) throw new AccessDeniedException("context_source_changed");
            if (message.Name != "variables.resolve") continue;
            var value = await variable(Variable.NormalizeName(witness.GetProperty("arguments").GetProperty("name").GetString()!)).ConfigureAwait(false);
            var result = body.GetProperty("result");
            if (value is null || value.Revision != result.GetProperty("revision").GetInt64() || result.GetProperty("name").GetString() != value.Name
                || result.GetProperty("kind").GetString() != value.Kind.ToString()) throw new AccessDeniedException("context_source_changed");
            if (value.Kind == EnvironmentVariableKind.Plain) continue;
            var id = value.SecretId ?? value.CredentialId!.Value; var kind = value.SecretId is not null ? SpaceBindingKind.Secret : SpaceBindingKind.Credential;
            if (!pin.SpaceBindings.Any(b => b.Kind == kind && b.ResourceKey == id.ToString("D"))) throw new AccessDeniedException("context_source_changed");
            if (kind == SpaceBindingKind.Secret)
            {
                if (!await secretAvailable(id).ConfigureAwait(false)) throw new AccessDeniedException("context_source_changed");
                await Permission("secrets.read", TenantSecretService.Scope(state.Tenant.TenantId, id)).ConfigureAwait(false);
                await Permission("secrets.use", TenantSecretService.Scope(state.Tenant.TenantId, id), eligibility: true).ConfigureAwait(false);
            }
            else
            {
                var bound = (await credentials().ConfigureAwait(false)).SingleOrDefault(b => b.Credential.Value.CredentialId == id
                    && b.Credential.Value.Status == AgentCore.Domain.Credentials.CredentialStatus.Active
                    && b.Binding.Reference == result.GetProperty("accountRef").GetString()
                    && pin.CredentialBindings.Any(p => p.BindingId == b.Binding.BindingId && p.CredentialId == id && p.Reference == b.Binding.Reference));
                if (bound is null) throw new AccessDeniedException("context_source_changed");
                await Permission("credentials.read", TenantCredentialRules.Scope(state.Tenant.TenantId, id)).ConfigureAwait(false);
                await Permission("credentials.use", TenantCredentialRules.Scope(state.Tenant.TenantId, id), eligibility: true).ConfigureAwait(false);
            }
        }
        async ValueTask Permission(string key, AccessScope target, bool eligibility = false)
        {
            foreach (var actor in run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId is { } human ? new[] { human, agent.PrincipalId } : new[] { agent.PrincipalId })
                if (!(await iam.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get(key), target, ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("context_source_changed");
            if (eligibility) return;
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, run.Admission.ExecutionContext.OriginalInitiatorPrincipalId, key, target, iam, ct).ConfigureAwait(false))
                throw new AccessDeniedException("context_review_required");
        }
    }
}
