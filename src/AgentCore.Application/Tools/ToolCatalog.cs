using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class ToolCatalog
{
    public const string KnowledgeRetrieve = "knowledge.retrieve";
    public const string AttachmentsRead = "attachments.read";
    public const string WorkspaceRead = "workspace.read";
    public const string WorkspaceList = "workspace.list";
    public const string WorkspaceWrite = "workspace.write";
    public const string WorkspacePatch = "workspace.patch";
    public const string WorkspaceSearch = "workspace.search";
    public const string WorkspaceRetain = "workspace.retain";
    public const string WorkspaceCheckout = "workspace.checkout";
    public const string WorkspaceMove = "workspace.move";
    public const string ArtifactsCreate = "artifacts.create";
    public const string ArtifactsCreateFromWorkspace = "artifacts.create_from_workspace";
    public const string ArtifactsVerify = "artifacts.verify";
    public const string SandboxRun = "sandbox.run";
    public const string WebSearch = "web.search";
    public const string WebFetch = "web.fetch";
    public const string HttpRequest = "http.request";
    public const string EmailSearch = "email.search";
    public const string EmailRead = "email.read";
    public const string EmailCreateDraft = "email.create_draft";
    public const string EmailSend = "email.send";
    public const string DemoSensitiveAction = "demo.sensitive_action";
    public const string TriggerScheduleOnce = "trigger.schedule_once";
    public const string TriggerScheduleRecurring = "trigger.schedule_recurring";
    public const string TriggerList = "trigger.list";
    public const string TriggerUpdate = "trigger.update";
    public const string TriggerCancel = "trigger.cancel";
    public const string SkillsLoad = "skills.load";
    public const string AppMessageSend = "app.message.send";
    public const string BrowserNavigate = "browser.navigate";
    public const string BrowserObserve = "browser.observe";
    public const string BrowserAct = "browser.act";
    public const string BrowserClose = "browser.close";
    public const string BrowserPages = "browser.pages";
    public const string BrowserCapture = "browser.capture";
    public const string ContinuitySearch = "continuity.search";
    public const string ContinuityGet = "continuity.get";
    public const string MemoryConsolidate = "memory.consolidate";
    public const string MemoryForget = "memory.forget";
    public const string ExperienceConsolidate = "experience.consolidate";
    public static bool IsIdentityMaintenance(string name) => name is MemoryConsolidate or MemoryForget or ExperienceConsolidate;
    public const string ExperienceRecent = "experience.recent";
    public const string WorkComplete = "work.complete";

    public static bool RecordsOwnerVisibleEffect(string toolName) =>
        toolName is not (BrowserNavigate or BrowserObserve or BrowserPages or BrowserCapture or BrowserClose);

    public static bool IsBrowserTool(string toolName) =>
        toolName is BrowserNavigate or BrowserObserve or BrowserAct or BrowserClose or BrowserPages or BrowserCapture;

    public static bool AuthorizesBrowser(IReadOnlyList<ModelToolDefinition>? tools) =>
        tools?.Any(tool => IsBrowserTool(tool.Name)) == true;

    public static IReadOnlyList<ModelToolDefinition> For(
        AgentDefinition definition,
        AgentContext? context,
        IToolConfigurationGate configurationGate)
    {
        if (context is not null && !context.ModelSupportsTools)
        {
            return [];
        }

        if (context?.Trigger.Kind is TriggerKind.ScheduledOccurrence or TriggerKind.ApplicationEvent)
        {
            if (context.TrustedConnection)
            {
                return OccurrenceTools(definition, context, configurationGate);
            }

            return context.Trigger.Kind == TriggerKind.ApplicationEvent
                ? UnconnectedApplicationTools(definition, context, configurationGate)
                : string.IsNullOrEmpty(context.ContinuityContext) ? [] : ContinuityOnly(definition, context, configurationGate);
        }

        var offered = new List<ModelToolDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in RoleEnvironments.Of(definition).ToolList)
        {
            if (name is WorkspaceRetain or WorkspaceCheckout && context is not { AgentWorkspaceAvailable: true, DetachedExecution: false }) continue;
            if (!ToolRegistry.TryGet(name, out var descriptor)
                || descriptor.OfferRule is not (ToolOfferRule.RoleAllowlist or ToolOfferRule.ConfigurationWhenRoleAllows)
                || !ToolPolicy.IsOffered(descriptor, definition, context, configurationGate)
                || !seen.Add(name))
            {
                continue;
            }

            offered.Add(descriptor.ModelDefinition);
        }

        if (ToolRegistry.TryGet(AttachmentsRead, out var attachmentDescriptor)
            && attachmentDescriptor.OfferRule == ToolOfferRule.SessionAttachmentsWhenRoleAllows
            && ToolPolicy.IsOffered(attachmentDescriptor, definition, context, configurationGate)
            && seen.Add(AttachmentsRead))
        {
            offered.Add(attachmentDescriptor.ModelDefinition);
        }

        foreach (var descriptor in ToolRegistry.All)
        {
            if (descriptor.OfferRule is not (ToolOfferRule.CurrentExecutionCapability or ToolOfferRule.HarnessAuthority)
                || !ToolPolicy.IsOffered(descriptor, definition, context, configurationGate)
                || !seen.Add(descriptor.Name))
            {
                continue;
            }

            offered.Add(descriptor.ModelDefinition);
        }

        AddWorkComplete(offered, seen, definition, context, configurationGate);
        return offered;
    }

    private static List<ModelToolDefinition> ContinuityOnly(AgentDefinition definition, AgentContext context, IToolConfigurationGate gate)
    {
        var offered = new List<ModelToolDefinition>();
        AddWorkComplete(offered, new(StringComparer.Ordinal), definition, context, gate);
        return offered;
    }

    private static List<ModelToolDefinition> OccurrenceTools(
        AgentDefinition definition,
        AgentContext context,
        IToolConfigurationGate configurationGate)
    {
        var offered = new List<ModelToolDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in RoleEnvironments.Of(definition).ToolList)
        {
            if (!ToolRegistry.TryGet(name, out var descriptor)
                || !ToolPolicy.IsOffered(descriptor, definition, context, configurationGate)
                || !seen.Add(name))
            {
                continue;
            }

            offered.Add(descriptor.ModelDefinition);
        }

        AddWorkComplete(offered, seen, definition, context, configurationGate);
        return offered;
    }

    private static List<ModelToolDefinition> UnconnectedApplicationTools(
        AgentDefinition definition,
        AgentContext context,
        IToolConfigurationGate configurationGate)
    {
        var offered = new List<ModelToolDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in RoleEnvironments.Of(definition).ToolList)
        {
            if (!string.Equals(name, DemoSensitiveAction, StringComparison.Ordinal)
                || !ToolRegistry.TryGet(name, out var descriptor)
                || !ToolPolicy.IsOffered(descriptor, definition, context, configurationGate)
                || !seen.Add(name))
            {
                continue;
            }

            offered.Add(descriptor.ModelDefinition);
        }

        AddWorkComplete(offered, seen, definition, context, configurationGate);
        return offered;
    }

    private static void AddWorkComplete(
        List<ModelToolDefinition> offered,
        HashSet<string> seen,
        AgentDefinition definition,
        AgentContext? context,
        IToolConfigurationGate configurationGate)
    {
        foreach (var name in new[] { ContinuitySearch, ContinuityGet, MemoryConsolidate, MemoryForget, ExperienceConsolidate })
            if (ToolRegistry.TryGet(name, out var continuityDescriptor)
                && ToolPolicy.IsOffered(continuityDescriptor, definition, context, configurationGate) && seen.Add(name))
                offered.Add(continuityDescriptor.ModelDefinition);
        if (!string.IsNullOrEmpty(context?.ExperienceContext) && ToolRegistry.TryGet(ExperienceRecent, out var experienceDescriptor)
            && ToolPolicy.IsOffered(experienceDescriptor, definition, context, configurationGate) && seen.Add(ExperienceRecent))
            offered.Add(experienceDescriptor.ModelDefinition);
        if (ToolRegistry.TryGet(WorkComplete, out var descriptor)
            && ToolPolicy.IsOffered(descriptor, definition, context, configurationGate)
            && seen.Add(WorkComplete))
        {
            offered.Add(context?.Trigger.Kind == TriggerKind.ThoughtActivation
                ? AgentCore.Application.Work.ThoughtCompletion.Contract : descriptor.ModelDefinition);
        }
    }

    public static bool OffersAttachmentRead(
        AgentDefinition definition,
        AgentContext context,
        IToolConfigurationGate configurationGate) =>
        ToolPolicy.IsOffered(definition, context, AttachmentsRead, configurationGate);

    public static bool SessionHasAttachments(AgentContext context) =>
        context.SessionAttachments is { Count: > 0 }
        || context.AttachmentContents is { Count: > 0 };

    public static bool IsPermittedForExecution(AgentDefinition definition, string toolName) =>
        RolePermissions.AllowsTool(definition, toolName);

    public static IEnumerable<string> AllKnownNames() => ToolRegistry.AllKnownNames();

    public static ToolEffect EffectOf(string toolName) =>
        ToolRegistry.TryGet(toolName, out var descriptor) ? descriptor.Effect : throw new KeyNotFoundException($"Unknown tool '{toolName}'.");

    public static ToolReplaySafety ReplaySafetyOf(string toolName) =>
        ToolRegistry.TryGet(toolName, out var descriptor)
            ? descriptor.ReplaySafety
            : throw new KeyNotFoundException($"Unknown tool '{toolName}'.");
}
