using System.Text;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    public async ValueTask<(ToolApprovalPreparation? Preparation, string? ErrorJson)> PrepareManagementApprovalAsync(
        ModelToolCall call, JsonElement args, ToolExecutionAdmission? admission, CancellationToken ct, Guid sessionId = default)
    {
        if (await LegacyApprovalPreparationErrorAsync(sessionId, admission, ct).ConfigureAwait(false) is { } denied)
            return (null, denied);
        if (admission?.AgentInstanceId is not Guid id || selfManagement is null)
            return (null, Error("forbidden", "Trusted Instance context is required."));
        try
        {
            if (ConfigurationTools.IsTool(call.Name)) ConfigurationTools.Validate(call.Name, args);
            if (OwnerOnlySandboxFields(call.Name, args)) throw AgentCoreErrors.Forbidden("Sandbox initialization and limit changes are managed in the owner Admin interface.");
            var policy = await selfManagement.ReadAsync(id, ct);
            var revision = args.GetProperty("expectedInstanceRevision").GetInt64();
            if (revision != policy.InstanceRevision) throw AgentCoreErrors.Conflict("Instance revision changed; inspect before approval.");
            if (call.Name == "configuration.propose")
            {
                var owner = await _agentInstances!.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Instance was not found.");
                var baseline = await _agentDefinitions!.GetAsync(owner.DefinitionId, owner.ActiveVersion, ct) ?? throw AgentCoreErrors.NotFound("Definition was not found.");
                var admitted = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(baseline, owner.SettingsOverrides) with { SelfManagement = policy.Effective };
                await ValidateTargetedRemedyAsync(id, policy, admitted, args, ct);
            }
            var details = new Dictionary<string, string> {
                ["Target"] = "This Agent Instance: " + id.ToString("D"), ["Instance revision"] = revision.ToString(),
                ["Policy revision"] = policy.PolicyRevision.ToString(), ["Applies to"] = call.Name == "management.freeze" ? "Immediate management revocation"
                    : call.Name == "management.policy.update" ? "Authority removals revoke immediately; new grants apply to the next new Run"
                    : "Next new Run, including existing Sessions; current Run remains pinned",
                ["Exact operation"] = call.Name, ["Approval scope"] = "This configuration only. Original sensitive task actions need their separate approvals." };
            if (call.Name == "automation.preferences.patch")
            {
                if (instanceAdmin is null) throw AgentCoreErrors.Validation("Automation model preferences are unavailable in this host.");
                instanceAdmin.ValidateUnattendedModel(args.GetProperty("catalogKey").GetString(), args.GetProperty("reasoningEffort").GetString());
                var owner = await _agentInstances!.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Instance was not found.");
                details["Before"] = JsonSerializer.Serialize(new { owner.UnattendedModelCatalogKey, owner.UnattendedReasoningEffort }, ManagementJson);
                details["After"] = args.GetRawText();
                details["Effect"] = "Change the default for future unattended Runs; current Runs and ordinary reminder authority are unchanged.";
            }
            else if (call.Name is "settings.patch" or "configuration.propose")
            {
                var state = await instanceSettings!.ReadAsync(id, args.GetProperty("section").GetString()!, ct);
                var set = args.GetProperty("set").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
                var clear = args.GetProperty("clear").EnumerateArray().Select(p => p.GetString()!).ToArray();
                // Preview and commit use the same owner, Definition and host constraints.
                await instanceSettings.ValidatePatchAsync(id, state.Section, revision, set, clear, ct);
                var fields = set.Keys.Concat(clear).ToArray();
                details["Before and source"] = JsonSerializer.Serialize(fields.ToDictionary(f => f, f => new { value = state.Effective[f], source = state.Sources[f] }), ManagementJson);
                details["After"] = JsonSerializer.Serialize(new { section = state.Section, set, clear, inheritedDefaults = clear.ToDictionary(f => f, f => state.DefinitionDefaults[f]) }, ManagementJson);
            }
            else if (call.Name == "skills.create")
            {
                var input = InstanceSkillTools.ReadInput(args);
                details["Target"] += " / " + input.Name;
                details["Source"] = "Current owner request; retained procedure grants no authority.";
                details["Effect"] = "Create an independent Instance Skill; required capabilities grant no authority.";
                details["Before"] = "No Skill is being replaced.";
                details["After"] = JsonSerializer.Serialize(input, ManagementJson);
            }
            else if (call.Name == "resources.upsert" && !args.TryGetProperty("key", out _))
            {
                details["Target"] += " / " + args.GetProperty("logicalPath").GetString();
                details["Effect"] = "Create an independent Instance resource; inherited bytes remain unchanged.";
                details["Before"] = "No resource is being replaced.";
                details["Source"] = args.GetProperty("source").GetString()!;
                details["Media type"] = args.GetProperty("mediaType").GetString()!;
                details["After"] = args.GetProperty("content").GetString()!;
            }
            else if (InstanceSkillTools.IsManagement(call.Name) && args.TryGetProperty("key", out var key))
            {
                var skill = await instanceSkills!.InspectAsync(id, key.GetString()!, ct: ct);
                if (skill.Revision != args.GetProperty("expectedRevision").GetInt64()) throw AgentCoreErrors.Conflict("Skill revision changed; inspect before approval.");
                details["Before"] = JsonSerializer.Serialize(skill, ManagementJson);
                details["After"] = args.GetRawText();
                details["Effect"] = call.Name == "skills.delete" ? "Delete this local Skill for future Runs."
                    : call.Name == "skills.customize" ? "Create an independent copy and disable the Definition source."
                    : "Change only the selected Skill for future Runs.";
            }
            else if (call.Name.StartsWith("resources.", StringComparison.Ordinal) && args.TryGetProperty("key", out var resourceKey))
            {
                var catalog = await instanceResources!.ListAsync(id, ct);
                var resource = catalog.Resources.SingleOrDefault(r => r.Key == resourceKey.GetString()) ?? throw AgentCoreErrors.NotFound("Resource was not found.");
                details["Before"] = JsonSerializer.Serialize(resource, ManagementJson); details["After"] = args.GetRawText();
                details["Effect"] = call.Name == "resources.delete" ? "Delete this Instance resource for future Runs."
                    : call.Name == "resources.copy" ? "Create an independent copy; source remains unchanged." : "Change the selected resource for future Runs.";
            }
            else if (call.Name == "credentials.binding.propose")
            {
                var credential = await credentials!.GetAsync(Guid.Parse(args.GetProperty("credentialId").GetString()!), ct);
                details["Existing credential"] = credential.DisplayName + " · " + credential.Kind;
                details["Binding alias"] = args.GetProperty("reference").GetString()!;
                details["Before"] = JsonSerializer.Serialize((await credentials.BindingsAsync(id, ct)).Select(b => new { b.Reference, b.CredentialId }), ManagementJson);
            }
            else if (call.Name == "credentials.binding.remove")
            {
                var binding = (await credentials!.BindingsAsync(id, ct)).SingleOrDefault(b => b.BindingId == Guid.Parse(args.GetProperty("bindingId").GetString()!))
                    ?? throw AgentCoreErrors.NotFound("Binding is unavailable for this Instance.");
                if (binding.Revision != args.GetProperty("expectedBindingRevision").GetInt64()) throw AgentCoreErrors.Conflict("Binding revision changed.");
                details["Before"] = JsonSerializer.Serialize(new { binding.BindingId, binding.Reference, binding.CredentialId, binding.Revision }, ManagementJson);
                details["After"] = "Binding removed; secret retained in System Credentials.";
                details["Applies to"] = "Immediate binding revocation";
            }
            else { details["Before"] = JsonSerializer.Serialize(policy.Effective, ManagementJson); details["After"] = args.GetRawText(); }
            foreach (var value in details.Values)
            {
                DefinitionResourcePolicies.RejectSecretsInTextualContent("text/plain", Encoding.UTF8.GetBytes(value));
                if (value.Length > 32000) throw AgentCoreErrors.Validation("Exact preview is too large; request a smaller change.");
            }
            return (new(ToolActionHash.Compute(call.Name, args), call.Name == "management.freeze" ? "Freeze self-management" : "Apply configuration to this Instance",
                call.ArgumentsJson, details), null);
        }
        catch (AgentCoreException e) { return (null, Error(e.Code, e.Message)); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException)
        { return (null, Error("invalid", "Cannot prepare an exact bounded preview. Inspect and correct the arguments.")); }
    }
}
