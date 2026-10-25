using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Definitions;

public sealed class FileBuiltInDefinitionTemplateSource(IBuiltInAgentDefinitionStore definitions, string agentDirectory, string templateRoot)
    : IBuiltInDefinitionTemplateSource
{
    private readonly CanonicalWorkspaceFilesystem _knowledge = new(Path.Combine(agentDirectory, "knowledge"), "/home");
    private readonly CanonicalWorkspaceFilesystem _templates = new(templateRoot, "/working");
    public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) => definitions.ListAsync(ct);

    public async ValueTask<BuiltInDefinitionTemplate?> ReadAsync(string templateId, int version, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var definition = await definitions.GetAsync(templateId, version, ct).ConfigureAwait(false);
        if (definition is null) return null;
        AgentDefinitionValidator.Validate(definition);
        var resources = new Dictionary<string, BuiltInDefinitionResource>(StringComparer.Ordinal);
        long bytes = 0;
        void Add(string path, AgentDefinitionResourceKind kind, string mediaType, byte[] content)
        {
            path = DefinitionResourcePolicies.NormalizeLogicalPath(path);
            mediaType = DefinitionResourcePolicies.NormalizeMediaType(mediaType);
            DefinitionResourcePolicies.ValidateContentSize(content.LongLength);
            DefinitionResourcePolicies.RejectSecretsInTextualContent(mediaType, content);
            if (resources.ContainsKey(path)) throw AgentCoreErrors.Validation("Shipped template has conflicting resource paths.");
            if (resources.Count >= AgentResourceLimits.MaxItemsPerDraft || bytes + content.LongLength > AgentResourceLimits.MaxAggregateBytes)
                throw AgentCoreErrors.Validation("Shipped template exceeds Definition resource limits.");
            resources.Add(path, new(path, kind, mediaType, content)); bytes += content.LongLength;
        }
        foreach (var source in DefinitionExecutionPolicies.Of(definition).KnowledgeList)
        {
            // Identity selects the trusted approved file; ResourcePath is only the draft binding path.
            if (string.IsNullOrWhiteSpace(source.Identity) || source.Identity.IndexOfAny(['/', '\\', '.', ':']) >= 0)
                throw AgentCoreErrors.Validation("Shipped knowledge identity is invalid.");
            Add(KnowledgeSourcePaths.ResolveBackingPath(source), AgentDefinitionResourceKind.Knowledge, "text/markdown",
                await _knowledge.ReadUnlinkedAsync("/home/" + source.Identity + ".md", AgentResourceLimits.MaxItemBytes, ct).ConfigureAwait(false));
        }
        if (DefinitionExecutionPolicies.Of(definition).WorkspacePolicy.TemplateId is { } workspaceTemplate)
        {
            var prefix = "/working/" + workspaceTemplate;
            var visited = 0;
            async ValueTask Copy(string path)
            {
                ct.ThrowIfCancellationRequested();
                if (++visited > 256) throw AgentCoreErrors.Validation("Shipped template exceeds its directory limit.");
                string? cursor = null;
                do
                {
                    var page = _templates.ListUnlinked(path, cursor, 65);
                    foreach (var entry in page.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        // Match first-provisioning policy: these two areas are not workspace files.
                        if (path == prefix && Path.GetFileName(entry.Path) is "knowledge" or "harness") continue;
                        if (entry.Directory) await Copy(entry.Path).ConfigureAwait(false);
                        else if (entry.Kind == "file")
                        {
                            var logical = entry.Path["/working/".Length..];
                            DefinitionResourcePolicies.NormalizeLogicalPath(logical);
                            Add(logical, AgentDefinitionResourceKind.Template, WorkspaceContentTypes.Of(logical),
                                await _templates.ReadUnlinkedAsync(entry.Path, AgentResourceLimits.MaxItemBytes, ct).ConfigureAwait(false));
                        }
                        else throw AgentCoreErrors.Validation("Shipped template resources must be ordinary files and directories.");
                    }
                    cursor = page.NextPath;
                } while (cursor is not null);
            }
            await Copy(prefix).ConfigureAwait(false);
        }
        // A Skill may reference approved knowledge/template bytes; other arbitrary agent-directory
        // files are not an implicit resource catalog. Missing approved dependencies fail closed.
        foreach (var path in definition.SkillList.SelectMany(s => s.ResourcePaths))
            if (!resources.ContainsKey(DefinitionResourcePolicies.NormalizeLogicalPath(path)))
                throw AgentCoreErrors.Validation("Shipped Skill references unavailable approved content.");
        return new(definition, resources.Values.OrderBy(r => r.LogicalPath, StringComparer.Ordinal).ToArray());
    }
}
