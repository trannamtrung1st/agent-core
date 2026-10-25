using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Security;

// Navigation and on-demand variable reads use the existing Run, IAM and Session owners.
// This service never decrypts protected values or creates a competing selection store.
public sealed class TenantContextToolService(TenantOperationAuthorizer operations, ITenantSessionStore sessions,
    ITenantSpaceStore spaces, ITenantEnvironmentStore environments, ITenantSecretStore secrets, IIamStore security,
    TenantRunConfigurationResolver configuration, TimeProvider time, Func<ITenantSessionContextControl>? contextControl = null, ITenantSandboxContextIsolation? sandboxIsolation = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Source(AgentRun Run, TenantSecurityState Security, TenantSession Session, Space Space);
    public async ValueTask<IReadOnlyList<string>> OfferedAsync(AgentRun run, CancellationToken ct = default)
    {
        if (run.Claim is not { } claim) return [];
        var source = await Require(new(run.Owner, run.SessionId, run.AgentRunId, claim.Generation), ct).ConfigureAwait(false);
        var current = (await configuration.CurrentPolicyAsync(source.Security.Tenant.TenantId, source.Run.AgentInstanceId, ct).ConfigureAwait(false)).Definition;
        var result = new List<string>();
        foreach (var name in TenantContextToolContract.Names)
        {
            if (!TenantContextToolContract.Allows(source.Run.Admission.Configuration!.Definition, name) || !TenantContextToolContract.Allows(current, name)) continue;
            if (name.StartsWith("environments.", StringComparison.Ordinal) || name.StartsWith("variables.", StringComparison.Ordinal))
            {
                if (!source.Space.EnvironmentsEnabled || source.Run.Admission.Configuration?.SpaceContext?.EnvironmentsEnabled != true) continue;
                if (!await Permitted(source, "environments.read", Scope(source), ct).ConfigureAwait(false)) continue;
            }
            if (name == "environments.deactivate" && source.Run.Admission.Configuration?.SpaceContext?.EnvironmentId is null) continue;
            if (name.StartsWith("variables.", StringComparison.Ordinal) && source.Run.Admission.Configuration?.SpaceContext?.EnvironmentId is null) continue;
            var permission = name switch { "variables.resolve" => "variables.resolve", "variables.list" => "variables.read", "environments.activate" or "environments.deactivate" => "environments.activate", _ => "spaces.read" };
            if (!await Permitted(source, permission, Scope(source), ct).ConfigureAwait(false)) continue;
            if (TenantContextToolContract.IsSwitch(name) && (!source.Run.Admission.Configuration!.SpaceContext!.MatchesSelection(source.Session.Snapshot.ContextSelection) || !CanSwitch(source.Run) || !await Permitted(source, "sessions.manage", TenantSessionRules.Scope(run.Owner.TenantId!.Value, run.SessionId), ct).ConfigureAwait(false))) continue;
            result.Add(name);
        }
        await Fence(source, ct).ConfigureAwait(false); return result;
    }
    public async ValueTask<string> ExecuteAsync(TenantRuntimeFrame frame, ModelToolCall call, int budget, CancellationToken ct = default)
    {
        if (!TenantContextToolContract.IsTool(call.Name) || Encoding.UTF8.GetByteCount(call.ArgumentsJson) > 4096) throw new ArgumentException("Invalid context tool.");
        using var document = JsonDocument.Parse(call.ArgumentsJson); var args = document.RootElement;
        using var schema = JsonDocument.Parse(TenantContextToolContract.Descriptors().Single(d => d.Name == call.Name).ModelDefinition.ParametersJson);
        var allowed = schema.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any(p => !allowed.Contains(p.Name)) || args.EnumerateObject().Select(p => p.Name).Distinct().Count() != args.EnumerateObject().Count()) throw new ArgumentException("Use the closed context arguments.");
        if (schema.RootElement.TryGetProperty("required", out var required) && required.EnumerateArray().Any(p => !args.TryGetProperty(p.GetString()!, out _))) throw new ArgumentException("Required context argument is missing.");
        foreach (var value in args.EnumerateObject())
        {
            var valid = value.Name switch {
                "spaceId" => value.Value.ValueKind == JsonValueKind.String && value.Value.TryGetGuid(out var id) && id != Guid.Empty,
                "environmentId" => value.Value.ValueKind == JsonValueKind.Null && call.Name == "spaces.activate" || value.Value.ValueKind == JsonValueKind.String && value.Value.TryGetGuid(out var id) && id != Guid.Empty,
                "expectedSelectionRevision" => value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt64(out var revision) && revision >= 1,
                "continueTask" => value.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "page" => value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out var page) && page is >= 1 and <= 10000,
                "name" or "after" => value.Value.ValueKind == JsonValueKind.String && value.Value.GetString()!.Length is >= 1 and <= 64,
                _ => false };
            if (!valid) throw new ArgumentException("Invalid typed context argument.");
        }
        var before = await Require(frame, ct).ConfigureAwait(false);
        if (!await Capability(before, call.Name, ct).ConfigureAwait(false)) throw new AccessDeniedException("capability_unavailable");
        if (TenantContextToolContract.IsSwitch(call.Name)) return await Switch(before, frame, call, args, ct).ConfigureAwait(false);
        object result; long? observedEnvironmentRevision = null;
        if (call.Name == "spaces.list")
        {
            var page = args.TryGetProperty("page", out var p) ? p.GetInt32() : 1;
            if (page is < 1 or > 10000) throw new ArgumentException("Invalid page.");
            var raw = await spaces.ListSpacesAsync(frame.Owner.TenantId!.Value, null, page - 1, 50, ct).ConfigureAwait(false); var items = new List<object>();
            foreach (var space in raw.Items)
                if (await Accessible(before, space, ct).ConfigureAwait(false)) items.Add(Metadata(space));
            result = new { items, page, hasMore = page * 50 < raw.Total, selectionRevision = before.Session.Snapshot.ContextSelection!.Revision };
        }
        else if (call.Name == "spaces.get")
        {
            var space = await spaces.FindSpaceAsync(frame.Owner.TenantId!.Value, GuidValue(args, "spaceId"), ct).ConfigureAwait(false);
            if (space is null || !await Accessible(before, space, ct).ConfigureAwait(false)) throw new AccessDeniedException("space_unavailable");
            result = new { space = Metadata(space), selectionRevision = before.Session.Snapshot.ContextSelection!.Revision };
        }
        else
        {
            var pin = before.Run.Admission.Configuration!.SpaceContext!;
            if (!pin.EnvironmentsEnabled || !before.Space.EnvironmentsEnabled) throw new AccessDeniedException("environments_disabled");
            await Permission(before, "environments.read", Scope(before), ct).ConfigureAwait(false);
            if (call.Name == "environments.list")
            {
                var profiles = await environments.ListEnvironmentsAsync(pin.TenantId, pin.SpaceId, ct).ConfigureAwait(false);
                result = new { items = profiles.Take(100).Select(e => new { e.EnvironmentId, e.Name, e.Description, e.Revision }), hasMore = profiles.Count > 100,
                    selectedEnvironmentId = pin.EnvironmentId, selectionRevision = before.Session.Snapshot.ContextSelection!.Revision };
            }
            else
            {
                if (pin.EnvironmentId is not { } environment) throw new AccessDeniedException("environment_not_selected");
                await Permission(before, call.Name == "variables.resolve" ? "variables.resolve" : "variables.read", Scope(before), ct).ConfigureAwait(false);
                observedEnvironmentRevision = (await environments.FindEnvironmentAsync(pin.TenantId, pin.SpaceId, environment, ct).ConfigureAwait(false)
                    ?? throw new AccessDeniedException("environment_unavailable")).Revision;
                var values = await environments.ReadVariablesAsync(pin.TenantId, pin.SpaceId, environment, ct).ConfigureAwait(false);
                if (call.Name == "variables.list")
                {
                    var after = args.TryGetProperty("after", out var a) ? AgentCore.Domain.Spaces.EnvironmentVariable.NormalizeName(a.GetString()!) : null;
                    var page = values.Where(v => after is null || string.CompareOrdinal(v.Name, after) > 0).OrderBy(v => v.Name, StringComparer.Ordinal).Take(51).ToArray();
                    result = new { items = page.Take(50).Select(v => new { v.Name, kind = v.Kind.ToString(), v.Description, v.Revision }), nextCursor = page.Length > 50 ? page[49].Name : null };
                }
                else
                {
                    var name = AgentCore.Domain.Spaces.EnvironmentVariable.NormalizeName(args.GetProperty("name").GetString()!);
                    var variable = values.SingleOrDefault(v => v.Name == name) ?? throw new AccessDeniedException("variable_unavailable");
                    result = await ResolveVariable(before, variable, ct).ConfigureAwait(false);
                }
            }
        }
        await Fence(before, ct).ConfigureAwait(false);
        return ToolJsonResults.FitToBudget(budget, JsonSerializer.Serialize(new { tenantContextRead = true, contextRead = new { tool = call.Name, arguments = args.Clone(), securityRevision = before.Security.Tenant.Revision,
            spaceId = before.Space.SpaceId, environmentId = before.Run.Admission.Configuration!.SpaceContext!.EnvironmentId, environmentRevision = observedEnvironmentRevision }, result }, Json));
    }
    private async ValueTask<object> ResolveVariable(Source source, AgentCore.Domain.Spaces.EnvironmentVariable variable, CancellationToken ct)
    {
        if (variable.Kind == EnvironmentVariableKind.Plain) return new { variable.Name, kind = "Plain", value = variable.PlainValue, variable.Revision };
        var pin = source.Run.Admission.Configuration!; var tenant = source.Security.Tenant.TenantId;
        var kind = variable.Kind == EnvironmentVariableKind.SecretReference ? SpaceBindingKind.Secret : SpaceBindingKind.Credential;
        var id = variable.SecretId ?? variable.CredentialId!.Value;
        if (!pin.SpaceBindings.Any(b => b.Kind == kind && b.ResourceKey == id.ToString("D"))) throw new AccessDeniedException("variable_resource_unavailable");
        if (kind == SpaceBindingKind.Secret)
        {
            var member = (await spaces.ReadMembershipsAsync(tenant, source.Space.SpaceId, ct).ConfigureAwait(false)).SingleOrDefault(m => m.InstanceId == source.Run.AgentInstanceId);
            if (member is null || !member.AddedBindings.Any(b => b.Kind == kind && b.ResourceKey == id.ToString("D")) || (await secrets.FindSecretAsync(tenant, id, ct).ConfigureAwait(false))?.Status != SecretStatus.Active) throw new AccessDeniedException("variable_resource_unavailable");
            await Permission(source, "secrets.read", TenantSecretService.Scope(tenant, id), ct).ConfigureAwait(false);
            await Eligibility(source, "secrets.use", TenantSecretService.Scope(tenant, id), ct).ConfigureAwait(false);
            return new { variable.Name, kind = "SecretReference", reasonCode = "unsupported_secure_use", secureUseSupported = false, variable.Revision };
        }
        var current = await configuration.CurrentCredentialBindingsAsync(source.Run, ct).ConfigureAwait(false);
        var binding = current.Bindings.SingleOrDefault(b => b.Credential.Value.CredentialId == id && b.Credential.Value.Status == AgentCore.Domain.Credentials.CredentialStatus.Active
            && pin.CredentialBindings.Any(p => p.BindingId == b.Binding.BindingId && p.CredentialId == id && p.Reference == b.Binding.Reference));
        if (binding is null) throw new AccessDeniedException("variable_resource_unavailable");
        await Permission(source, "credentials.read", TenantCredentialRules.Scope(tenant, id), ct).ConfigureAwait(false);
        await Eligibility(source, "credentials.use", TenantCredentialRules.Scope(tenant, id), ct).ConfigureAwait(false);
        return new { variable.Name, kind = "CredentialReference", accountRef = binding.Binding.Reference, purpose = binding.Credential.Value.Purpose.ToString(), variable.Revision, authorizationRequiredAtConsumer = true,
            nextStep = "Use this alias only with the existing authorized account or Browser consumer. That operation separately checks current permissions and exact consent." };
    }
    public async ValueTask RequireStoredAsync(AgentRun run, CancellationToken ct)
    {
        if (!AgentCore.Application.Execution.AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return;
        foreach (var message in messages!.Where(m => m.Role == ModelRole.Tool && TenantContextToolContract.IsTool(m.Name ?? "") && !TenantContextToolContract.IsSwitch(m.Name!)))
        {
            using var json = JsonDocument.Parse(message.Text);
            if (!json.RootElement.TryGetProperty("contextRead", out var read)) continue;
            var source = await Require(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), ct).ConfigureAwait(false);
            if (!await Capability(source, message.Name!, ct).ConfigureAwait(false)) throw new AccessDeniedException("capability_unavailable");
            if (message.Name!.StartsWith("variables.", StringComparison.Ordinal))
            {
                await Permission(source, message.Name == "variables.list" ? "variables.read" : "variables.resolve", Scope(source), ct).ConfigureAwait(false);
                var location = run.Admission.Configuration!.SpaceContext!;
                var environment = await environments.FindEnvironmentAsync(location.TenantId, location.SpaceId, location.EnvironmentId!.Value, ct).ConfigureAwait(false);
                if (!read.TryGetProperty("environmentRevision", out var revision) || revision.ValueKind != JsonValueKind.Number || environment?.Revision != revision.GetInt64())
                    throw new AccessDeniedException("context_source_changed");
                if (message.Name == "variables.resolve")
                {
                    var pin = run.Admission.Configuration!.SpaceContext!;
                    var variable = (await environments.ReadVariablesAsync(pin.TenantId, pin.SpaceId, pin.EnvironmentId!.Value, ct).ConfigureAwait(false)).SingleOrDefault(v => v.Name == AgentCore.Domain.Spaces.EnvironmentVariable.NormalizeName(read.GetProperty("arguments").GetProperty("name").GetString()!)) ?? throw new AccessDeniedException("variable_unavailable");
                    if (variable.Revision != json.RootElement.GetProperty("result").GetProperty("revision").GetInt64()) throw new AccessDeniedException("context_source_changed");
                    _ = await ResolveVariable(source, variable, ct).ConfigureAwait(false);
                }
            }
        }
    }
    private async ValueTask<string> Switch(Source before, TenantRuntimeFrame frame, ModelToolCall call, JsonElement args, CancellationToken ct)
    {
        if (!CanSwitch(before.Run) || contextControl is null) throw new AccessDeniedException("context_switch_unavailable");
        var captured = before.Run.Admission.Configuration!.SpaceContext!;
        var expected = args.GetProperty("expectedSelectionRevision").GetInt64();
        if (expected != captured.SelectionRevision) throw new SecurityConflictException("context_selection_changed");
        var spaceId = call.Name == "spaces.activate" ? GuidValue(args, "spaceId") : captured.SpaceId;
        var environmentId = call.Name == "environments.deactivate" ? (Guid?)null
            : args.TryGetProperty("environmentId", out var e) && e.ValueKind != JsonValueKind.Null ? GuidValue(args, "environmentId") : null;
        if (call.Name == "environments.activate" && environmentId is null) throw new ArgumentException("Environment is required.");
        var destination = await spaces.FindSpaceAsync(captured.TenantId, spaceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("space_unavailable");
        var member = (await spaces.ReadMembershipsAsync(captured.TenantId, spaceId, ct).ConfigureAwait(false)).SingleOrDefault(m => m.InstanceId == before.Run.AgentInstanceId);
        var profile = environmentId is { } id ? await environments.FindEnvironmentAsync(captured.TenantId, spaceId, id, ct).ConfigureAwait(false) : null;
        if (call.Name == "environments.deactivate" && captured.EnvironmentId is null) throw new AccessDeniedException("environment_not_selected");
        if (call.Name.StartsWith("environments.", StringComparison.Ordinal) && (!captured.EnvironmentsEnabled || !destination.EnvironmentsEnabled)) throw new AccessDeniedException("environments_disabled");
        var transition = new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"AgentCore.ContextSwitch\0{before.Run.AgentRunId:D}\0{call.Id}\0{call.Name}\0{call.ArgumentsJson}")).AsSpan(0, 16));
        var request = new TenantContextSwitch(before.Session.Owner, null, transition, spaceId, environmentId, environmentId is null ? 0 : destination.EnvironmentSelectionEpoch,
            before.Security.Tenant.Revision, before.Session.Snapshot.Revision, expected, before.Run.Admission.Configuration.InstanceRevision, frame, call.Name);
        var replay = TenantSessionContextRules.IsReplay(request, before.Session.Snapshot.ContextSelection);
        if (!replay && !captured.MatchesSelection(before.Session.Snapshot.ContextSelection)) throw new SecurityConflictException("context_selection_changed");
        var agent = await ((ITenantAgentStore)security).FindTenantAgentAsync(captured.TenantId, before.Run.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("executor_unavailable");
        request = request with { ExpectedInstanceRevision = agent.Instance.Revision };
        var check = replay ? request with { ExpectedSelectionRevision = before.Session.Snapshot.ContextSelection!.Revision } : request;
        await TenantSessionContextRules.RequireAgentAsync(check, before.Session, agent, before.Run, (await configuration.BaselineAsync(captured.TenantId, before.Run.AgentInstanceId, ct).ConfigureAwait(false)).Definition,
            before.Security, new IamAuthorizer(security), time.GetUtcNow(), ct).ConfigureAwait(false);
        await TenantSessionContextRules.RequireAsync(check, before.Session, agent, before.Security, new IamAuthorizer(security), destination, member, profile, true, ct).ConfigureAwait(false);
        if (!replay && sandboxIsolation is not null) await sandboxIsolation.RetireIdleGenerationAsync(before.Session.Owner, ct).ConfigureAwait(false);
        var result = await contextControl().SwitchContextAsync(request, ct).ConfigureAwait(false);
        var continueTask = args.TryGetProperty("continueTask", out var requested) && requested.GetBoolean();
        return JsonSerializer.Serialize(new { state = result.State, selection = result.Selection, contextTransition = new { transitionId = transition,
            sourceRunId = before.Run.AgentRunId, continueTask, selectionRevision = result.Selection.Revision, spaceId, environmentId },
            nextStep = continueTask ? "Finish this turn without effects in the new context. A single fresh successor may continue the original task after this Run completes safely." : "Selection applies to the next turn. Remaining effects in this Run retain their original context." }, Json);
    }
    private async ValueTask<Source> Require(TenantRuntimeFrame frame, CancellationToken ct)
    {
        var (run, state) = await operations.ResolveAsync(new(frame.Owner.TenantId!.Value, frame.RunId), ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(state.Tenant.TenantId, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("session_unavailable");
        TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
        var space = await spaces.FindSpaceAsync(state.Tenant.TenantId, run.Admission.Configuration!.SpaceContext!.SpaceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("space_unavailable");
        return new(run, state, session, space);
    }
    private async ValueTask<bool> Capability(Source source, string tool, CancellationToken ct) => TenantContextToolContract.Allows(source.Run.Admission.Configuration!.Definition, tool)
        && TenantContextToolContract.Allows((await configuration.CurrentPolicyAsync(source.Security.Tenant.TenantId, source.Run.AgentInstanceId, ct).ConfigureAwait(false)).Definition, tool);
    private async ValueTask<bool> Accessible(Source source, Space space, CancellationToken ct) => (space.Kind != SpaceKind.Private || space.OwnerInstanceId == source.Run.AgentInstanceId)
        && (await spaces.ReadMembershipsAsync(space.TenantId, space.SpaceId, ct).ConfigureAwait(false)).Any(m => m.InstanceId == source.Run.AgentInstanceId)
        && await Permitted(source, "spaces.read", TenantSpaceService.Scope(space.TenantId, space.SpaceId), ct).ConfigureAwait(false);
    private async ValueTask<bool> Permitted(Source source, string key, AccessScope scope, CancellationToken ct) =>
        (await operations.Evaluate(source.Security, source.Run.Admission.ExecutionContext!, key, scope, ct).ConfigureAwait(false)).Decision == OperationDecisionKind.Allowed;
    private async ValueTask Permission(Source source, string key, AccessScope scope, CancellationToken ct)
    {
        var decision = await operations.Check(source.Security, source.Run, key, scope, ct, purpose: ExecutionAuthorizationPurpose.ToolBoundary).ConfigureAwait(false);
        if (decision.Decision != OperationDecisionKind.Allowed) throw new AccessDeniedException(decision.Decision == OperationDecisionKind.RequiresApproval ? "context_review_required" : decision.ReasonCode);
    }
    private async ValueTask Eligibility(Source source, string key, AccessScope scope, CancellationToken ct)
    {
        // This returns reference metadata only. Actual use and its Review/ExceptionalGrant
        // stay with the existing protected consumer and exact effect receipt.
        foreach (var actor in source.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId is { } human
            ? new[] { human, source.Run.Owner.ExecutorPrincipalId!.Value } : new[] { source.Run.Owner.ExecutorPrincipalId!.Value })
            if (!(await new IamAuthorizer(security).CheckOwnedTenantAsync(source.Security, actor, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("variable_resource_unavailable");
    }
    private async ValueTask Fence(Source before, CancellationToken ct)
    {
        var after = await Require(new(before.Run.Owner, before.Run.SessionId, before.Run.AgentRunId, before.Run.Claim!.Generation), ct).ConfigureAwait(false);
        if (before.Run.Revision != after.Run.Revision || before.Security.Tenant.Revision != after.Security.Tenant.Revision || before.Session.Snapshot.Revision != after.Session.Snapshot.Revision)
            throw new SecurityConflictException("context_source_changed");
    }
    private static AccessScope Scope(Source source) => TenantSpaceService.Scope(source.Space.TenantId, source.Space.SpaceId);
    private static object Metadata(Space space) => new { space.SpaceId, space.Name, space.Description, kind = space.Kind.ToString(), space.EnvironmentsEnabled, space.Revision };
    private static Guid GuidValue(JsonElement args, string key) => args.GetProperty(key).TryGetGuid(out var id) && id != Guid.Empty ? id : throw new ArgumentException("Use a nonempty ID.");
    private static bool CanSwitch(AgentRun run) => run.Admission.OutputContract == AgentRunOutputContract.ConversationResponse
        && run.Admission.ExecutionContext is { OriginalInitiatorPrincipalId: not null, Origin: ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation };
}
