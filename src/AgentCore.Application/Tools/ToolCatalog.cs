using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class ToolCatalog
{
    public const string CredentialsList = "credentials.list";
    public const string KnowledgeRetrieve = "knowledge.retrieve";
    public const string AttachmentsRead = "attachments.read";
    public const string WorkspaceCwd = "workspace.cwd";
    public const string WorkspaceRead = "workspace.read";
    public const string WorkspaceList = "workspace.list";
    public const string WorkspaceWrite = "workspace.write";
    public const string WorkspacePatch = "workspace.patch";
    public const string WorkspaceSearch = "workspace.search";
    public const string WorkspaceMkdir = "workspace.mkdir";
    public const string WorkspaceCopy = "workspace.copy";
    public const string WorkspaceDelete = "workspace.delete";
    public const string WorkspaceBatch = "workspace.batch";
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
    public const string AutomationCreate = "automation.create";
    public const string AutomationList = "automation.list";
    public const string AutomationInspect = "automation.inspect";
    public const string AutomationRun = "automation.run";
    public const string AutomationDisable = "automation.disable";
    public const string AutomationUpdate = "automation.update";
    public const string AutomationDelete = "automation.delete";
    public const string CapabilitiesLoad = "capabilities.load";
    public const string SkillsLoad = "skills.load";
    public const string AppMessageSend = "app.message.send";
    public const string BrowserNavigate = "browser.navigate";
    public const string BrowserSnapshot = "browser.snapshot";
    public const string BrowserClose = "browser.close";
    public const string BrowserTabs = "browser.tabs";
    public const string BrowserScreenshot = "browser.screenshot";
    public const string BrowserFind = "browser.find";
    public const string BrowserClick = "browser.click";
    public const string BrowserHover = "browser.hover";
    public const string BrowserDrag = "browser.drag";
    public const string BrowserDrop = "browser.drop";
    public const string BrowserType = "browser.type";
    public const string BrowserFillForm = "browser.fill_form";
    public const string BrowserSelectOption = "browser.select_option";
    public const string BrowserPressKey = "browser.press_key";
    public const string BrowserUpload = "browser.upload";
    public const string BrowserFillCredential = "browser.fill_credential";
    public const string BrowserWait = "browser.wait_for";
    public const string BrowserDialog = "browser.dialog";
    public const string BrowserResize = "browser.resize";
    public const string BrowserConsole = "browser.console_messages";
    public const string BrowserStorageState = "browser.storage_state";
    public const string BrowserVisionMouse = "browser.mouse";
    public const string BrowserPdf = "browser.pdf";
    public const string BrowserTrace = "browser.trace";
    public const string BrowserHighlight = "browser.highlight";
    public const string BrowserMedia = "browser.emulate_media";
    public const string BrowserVideo = "browser.video";
    public const string BrowserEvaluate = "browser.evaluate";
    public const string BrowserConfiguration = "browser.get_config";
    public const string BrowserGeolocation = "browser.set_geolocation";
    public const string ContinuitySearch = "continuity.search";
    public const string ContinuityGet = "continuity.get";
    public const string MemoryConsolidate = "memory.consolidate";
    public const string MemoryForget = "memory.forget";
    public const string ExperienceConsolidate = "experience.consolidate";
    public static bool IsIdentityMaintenance(string name) => name is MemoryConsolidate or MemoryForget or ExperienceConsolidate;
    public const string ExperienceRecent = "experience.recent";
    public const string WorkComplete = "work.complete";

    public static bool RecordsOwnerVisibleEffect(string toolName) =>
        BrowserToolCatalog.TryGet(toolName, out var browser)
            ? browser.Effect != ToolEffect.ReadOnly && browser.Feature != BrowserFeature.Close
            : true;

    public static bool IsBrowserTool(string toolName) => BrowserToolCatalog.Tools.ContainsKey(toolName);

    public static bool AuthorizesBrowser(IReadOnlyList<ModelToolDefinition>? tools) =>
        tools?.Any(tool => IsBrowserTool(tool.Name)) == true;

    public static IReadOnlyList<ModelToolDefinition> For(AgentDefinition definition, AgentContext? context, IToolConfigurationGate gate) =>
        ToolProjectionService.Project(definition, context, gate);

    internal static IReadOnlyList<ModelToolDefinition> Eligible(
        AgentDefinition definition,
        AgentContext? context,
        IToolConfigurationGate configurationGate)
    {
        if (context is not null && !context.ModelSupportsTools)
        {
            return [];
        }

        if (context is not null && ToolResources.IsOccurrence(context.Trigger.Kind))
            return OccurrenceTools(definition, context, configurationGate);

        var offered = new List<ModelToolDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in RoleEnvironments.Of(definition).ToolList)
        {
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

        AddContextTools(offered, seen, definition, context, configurationGate);
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

        AddContextTools(offered, seen, definition, context, configurationGate);
        return offered;
    }

    private static void AddContextTools(
        List<ModelToolDefinition> offered,
        HashSet<string> seen,
        AgentDefinition definition,
        AgentContext? context,
        IToolConfigurationGate configurationGate)
    {
        foreach (var authority in ToolRegistry.All)
            if (authority.OfferRule is ToolOfferRule.CurrentExecutionCapability or ToolOfferRule.HarnessAuthority
                && ToolPolicy.IsOffered(authority, definition, context, configurationGate) && seen.Add(authority.Name))
                offered.Add(authority.ModelDefinition);
        foreach (var name in new[] { CredentialsList, CapabilitiesLoad, ContinuitySearch, ContinuityGet, MemoryConsolidate, MemoryForget, ExperienceConsolidate, AgentCore.Application.Experience.ExperienceService.SourceTool, AgentCore.Application.Experience.ExperienceService.RecordTool })
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
            offered.Add(AgentCore.Application.Work.WorkCompletionRequest.Contract);
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
