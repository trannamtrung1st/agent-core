using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantConfigurationRemedy(string Option, string Operation, string Section,
    IReadOnlyDictionary<string, JsonElement> Set, IReadOnlyList<string> Clear, string Target, string Goal, long ExpectedInstanceRevision);

// Projects the incumbent owned configuration. It neither reads secret payloads nor executes a remedy.
public sealed partial class TenantManagementInspectionProjection(IToolConfigurationGate tools, IDefinitionResourceContentStore? content = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async ValueTask<PreparedManagementRead> BuildAsync(TenantManagementReader reader, TenantManagementReadArguments args,
        IamAuthorizer iam, CancellationToken ct)
    {
        if (TenantManagementReadContract.IsChildTool(args.Tool)) return await BuildChildrenAsync(reader, args, iam, ct).ConfigureAwait(false);
        var catalog = reader.Inspection ?? throw new AccessDeniedException("management_inspection_unavailable");
        using var document = JsonDocument.Parse(args.Json); var arguments = document.RootElement;
        var owner = reader.Agent.Instance; var current = reader.Current; var policy = ManagementPolicyResolver.Resolve(reader.Baseline, owner);
        var pinned = reader.Run.Admission.Configuration!.Definition;
        bool Area(HarnessManagementScope area) => policy.Allows(area) && pinned.SelfManagement?.Allows(area) == true;
        var targeted = arguments.TryGetProperty("goal", out _) || arguments.TryGetProperty("target", out _);
        var target = Target(arguments);
        var supported = target is not null && ToolRegistry.TryGet(target, out var targetDescriptor) && targetDescriptor.DefinitionAuthorizable;
        var connection = !targeted || target == ToolCatalog.BrowserFillCredential;
        var selected = new List<TenantManagementInspectionItem>();
        async ValueTask<JsonElement[]> Select(string kind, bool allowed, int limit, bool knowledgeOnly = false)
        {
            if (!allowed) return [];
            var result = new List<JsonElement>();
            foreach (var item in catalog.Items.Where(i => i.Kind == kind && (!knowledgeOnly || i.Metadata.GetProperty("kind").GetString() == "Knowledge")))
            {
                if (!(await iam.CheckOwnedTenantAsync(reader.Security, reader.Agent.PrincipalId, PermissionCatalog.Get(TenantManagementInspectionSources.Permission(kind)), item.Scope, ct).ConfigureAwait(false)).Allowed) continue;
                selected.Add(item); result.Add(item.Metadata);
                if (result.Count == limit) break;
            }
            return result.ToArray();
        }
        var harness = args.Tool == "harness.inspect";
        var skills = await Select("skill", !targeted && Area(HarnessManagementScope.Skills), harness ? 6 : 4);
        var resources = await Select("resource", !targeted && Area(harness ? HarnessManagementScope.KnowledgeResources : HarnessManagementScope.Resources), harness ? 6 : 4, knowledgeOnly: harness);
        var bindings = await Select("binding", !harness && connection && Area(HarnessManagementScope.Connections), 4);
        var credentials = await Select("credential", !harness && connection && Area(HarnessManagementScope.Connections), 4);
        object data;
        if (harness)
        {
            data = new {
                activeDefinitionVersion = owner.ActiveVersion, currentRunDefinitionVersion = reader.Run.DefinitionVersion,
                instanceRevision = owner.Revision, policyRevision = owner.HarnessManagement?.PolicyRevision ?? 1, changesApplyToNextRun = true,
                mode = policy.Mode.ToString(), scopes = policy.Scopes.Select(s => s.ToString()),
                authoringEligibleTools = Area(HarnessManagementScope.ToolSelection) ? policy.EligibleTools.Where(t => tools.IsConfigured(t) && ToolRegistry.TryGet(t, out var descriptor) && descriptor.DefinitionAuthorizable && RolePermissions.AllowsTool(reader.Baseline, t)).ToArray() : [],
                activeDefinitionAuthorizedCapabilities = Area(HarnessManagementScope.ToolSelection) ? RoleEnvironments.Of(reader.Baseline).ToolList : [],
                activeAgentSelectedCapabilities = Area(HarnessManagementScope.ToolSelection) ? RoleEnvironments.Of(current).ToolList : [],
                instructions = Area(HarnessManagementScope.Instructions) ? ToolJsonResults.ClipUtf8Prefix(current.AgentInstructions, 1024) : null,
                instructionsTruncated = Area(HarnessManagementScope.Instructions) && System.Text.Encoding.UTF8.GetByteCount(current.AgentInstructions) > 1024,
                knowledge = resources, skills,
                metadataOnly = true, metadataWindow = 6,
                disclosure = "Only independently readable metadata in delegated areas is shown. Skill procedures and resource bytes require their own read tools. Omitted items grant no authority.",
                activation = "Changes customize this Agent only and apply to the next new Run, including existing Sessions. Current Runs remain pinned. Shared publication requires separate human review." };
        }
        else
        {
            var sections = Area(HarnessManagementScope.Settings) && (!targeted || supported) ? InstanceSettingsResolver.Sections
                .Where(section => !targeted || section == (target == "settings.patch" ? "conversationPolicy" : "capabilities")) : [];
            var settings = sections.Select(section => {
                var view = AgentInstanceSettingsService.View(owner, reader.Baseline, section);
                return new { section, effective = view.Effective.Where(p => section != "sandboxPolicy" || p.Key == "profileId").ToDictionary(p => p.Key, p => p.Value),
                    sources = view.Sources.Where(p => section != "sandboxPolicy" || p.Key == "profileId").ToDictionary(p => p.Key, p => p.Value) };
            }).ToArray();
            var remedy = targeted && Area(HarnessManagementScope.Settings) ? Remedy(reader, arguments) : null;
            var remedyTooLarge = remedy is not null && System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(remedy, Json)) > 8000;
            if (remedyTooLarge) remedy = null;
            object? blocker = null;
            var path = "/t/" + reader.Security.Tenant.TenantId.ToString("D") + "/admin/agents/" + owner.InstanceId.ToString("D") + "/settings";
            if (target is not null)
            {
                var registered = ToolRegistry.TryGet(target, out var descriptor) && descriptor.DefinitionAuthorizable;
                var authorized = registered && RolePermissions.AllowsTool(reader.Baseline, target);
                var enabled = authorized && RolePermissions.AllowsTool(current, target);
                var configured = tools.IsConfigured(target);
                var admitted = RolePermissions.AllowsTool(pinned, target);
                var reason = remedyTooLarge ? "exact_remedy_budget" : !registered ? "unsupported_target" : target == "settings.patch" && !Area(HarnessManagementScope.Settings) ? "management_scope_required" : !authorized ? "definition_limit" : !configured ? "host_setup_required" : !enabled ? "instance_selection" : !admitted ? "current_run_pinned" : "configuration_available";
                if (reason == "configuration_available" && target == ToolCatalog.BrowserFillCredential && Area(HarnessManagementScope.Connections)
                    && !bindings.Any(b => b.GetProperty("kind").GetString() == "Password")) reason = "readable_credential_binding_missing";
                blocker = new { target, reason, outcome = reason == "instance_selection" ? "instance_restriction" : reason is "configuration_available" ? "configuration_ready" : "human_action_required",
                    adminPath = path, nextStep = reason == "configuration_available" ? "Use the ordinary tool under its own current IAM, source and approval checks. Configuration readiness grants no execution authority."
                        : reason == "instance_selection" ? "Review the exact returned capability selection remedy; new capability grants apply to a new Run."
                        : reason == "current_run_pinned" ? "Start a new Run to use the current configuration. This observation does not widen this Run."
                        : "Use authorized Agent settings or Connections. Definition limits, host setup, OAuth and MFA require an authorized human." };
            }
            else if (targeted) blocker = new { reason = "unsupported_goal", outcome = "human_action_required", adminPath = path,
                nextStep = "Name an actual registered blocked tool. Core cannot infer a safe settings change from this goal." };
            data = new { outcome = "inspected", instanceRevision = owner.Revision, policyRevision = owner.HarnessManagement?.PolicyRevision ?? 1,
                changesApplyTo = "nextRun", settings, skills, resources, bindings, eligibleCredentials = credentials, blocker,
                remediationOptions = remedy is null ? Array.Empty<TenantConfigurationRemedy>() : [remedy], metadataOnly = true, metadataWindow = 4,
                disclosure = "Only independently readable child metadata in delegated areas is shown. No Skill procedure, resource bytes, credential metadata body or secret is included. Lists are bounded windows.",
                ownerOnly = "Provider secrets, OAuth/MFA, host restrictions and Definition-denied authority require authorized human setup." };
        }
        var sources = JsonSerializer.Serialize(selected.Select(i => new TenantManagementChildRead(i.Kind, i.Key, i.Fingerprint, i.Scope.ResourceId!)).ToArray());
        return TenantManagementReadRules.Build(reader, reader.Baseline, args, data, sources);
    }
    public static string? Target(JsonElement args) => TenantManagementReadSelection.Target(args);
    public TenantConfigurationRemedy? Remedy(TenantManagementReader reader, JsonElement args)
    {
        var target = Target(args); if (target is null) return null;
        var owner = reader.Agent.Instance; var policy = ManagementPolicyResolver.Resolve(reader.Baseline, owner);
        if (!policy.Allows(HarnessManagementScope.Settings) || reader.Run.Admission.Configuration!.Definition.SelfManagement?.Allows(HarnessManagementScope.Settings) != true) return null;
        var goal = args.TryGetProperty("goal", out var g) ? g.GetString()! : target;
        string section; Dictionary<string, JsonElement> set;
        if (target == "settings.patch" && (goal.Contains("brief", StringComparison.OrdinalIgnoreCase) || goal.Contains("concise", StringComparison.OrdinalIgnoreCase)))
        { section = "conversationPolicy"; set = new() { ["responseLength"] = JsonSerializer.SerializeToElement("concise") }; }
        else
        {
            if (!ToolRegistry.TryGet(target, out var descriptor) || !descriptor.DefinitionAuthorizable || !RolePermissions.AllowsTool(reader.Baseline, target)
                || RolePermissions.AllowsTool(reader.Current, target) || !tools.IsConfigured(target)) return null;
            section = "capabilities";
            var values = AgentInstanceSettingsService.View(owner, reader.Baseline, section).Effective["selected"];
            var selected = JsonSerializer.SerializeToElement(values).EnumerateArray().Select(v => v.GetString()!).Append(target).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            set = new() { ["selected"] = JsonSerializer.SerializeToElement(selected) };
        }
        var option = TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(new { id = owner.InstanceId, instanceRevision = owner.Revision,
            policyRevision = owner.HarnessManagement?.PolicyRevision ?? 1, target, goal, section, set = set.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value), clear = Array.Empty<string>() }));
        return new(option, "settings.patch", section, set, [], target, goal, owner.Revision);
    }
}
