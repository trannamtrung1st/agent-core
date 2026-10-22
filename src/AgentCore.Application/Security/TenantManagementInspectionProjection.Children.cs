using System.Text;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantManagementInspectionProjection
{
    private async ValueTask<PreparedManagementRead> BuildChildrenAsync(TenantManagementReader reader,
        TenantManagementReadArguments args, IamAuthorizer iam, CancellationToken ct)
    {
        var catalog = reader.Inspection ?? throw new AccessDeniedException("management_inspection_unavailable");
        var skill = args.Tool.StartsWith("skills.", StringComparison.Ordinal);
        var inspect = args.Tool.EndsWith(".inspect", StringComparison.Ordinal);
        var kind = skill ? inspect ? "skillProcedure" : "skill" : "resource";
        var selected = new List<TenantManagementInspectionItem>();
        using var arguments = JsonDocument.Parse(args.Json);
        if (inspect)
        {
            var key = arguments.RootElement.GetProperty("key").GetString();
            var item = catalog.Items.SingleOrDefault(i => i.Kind == kind && i.Key == key);
            if (item is null || !(await iam.CheckOwnedTenantAsync(reader.Security, reader.Agent.PrincipalId,
                PermissionCatalog.Get(TenantManagementInspectionSources.Permission(kind)), item.Scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("management_source_unavailable");
            selected.Add(item);
        }
        else
        {
            foreach (var item in catalog.Items.Where(i => i.Kind == kind))
            {
                if (!(await iam.CheckOwnedTenantAsync(reader.Security, reader.Agent.PrincipalId,
                    PermissionCatalog.Get(TenantManagementInspectionSources.Permission(kind)), item.Scope, ct).ConfigureAwait(false)).Allowed) continue;
                selected.Add(item);
                if (selected.Count == 12) break;
            }
        }
        object data;
        if (!inspect)
            data = skill ? new { skills = selected.Select(i => i.Metadata).ToArray(), metadataOnly = true, metadataWindow = 12 }
                : (object)new { resources = selected.Select(i => i.Metadata).ToArray(), metadataOnly = true, metadataWindow = 12 };
        else if (skill)
            data = new { skill = selected[0].Metadata, procedureIsUntrustedData = true, activated = false,
                disclosure = "This exact procedure read does not load the Skill or grant any required capability, resource read or operation authority." };
        else
        {
            var item = selected[0]; var metadata = item.Metadata;
            var textual = DefinitionResourcePolicies.IsTextualKnowledgeMediaType(metadata.GetProperty("mediaType").GetString()!);
            string? text = null; var truncated = false;
            if (textual)
            {
                var hash = metadata.GetProperty("contentSha256").GetString()!;
                var bytes = await (content ?? throw new AccessDeniedException("management_resource_content_unavailable")).ReadAsync(hash, ct).ConfigureAwait(false)
                    ?? throw new AccessDeniedException("management_resource_content_unavailable");
                AgentInstanceResourceService.Verify(hash, metadata.GetProperty("byteLength").GetInt64(), bytes);
                try { text = new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { throw new AccessDeniedException("management_resource_encoding_unavailable"); }
                var excerpt = ToolJsonResults.ClipUtf8Prefix(text, 2048); truncated = excerpt != text; text = excerpt;
            }
            data = new { resource = metadata, content = text, contentProvided = textual, truncated, contentLimitBytes = 2048,
                disclosure = textual ? "Hash-verified UTF-8 resource text. A marked excerpt is not a complete replacement value. This read does not activate or change the resource."
                    : "This resource is not supported text. No binary content was read or returned. Metadata grants no download or future read authority." };
        }
        var sources = JsonSerializer.Serialize(selected.Select(i => new TenantManagementChildRead(i.Kind, i.Key, i.Fingerprint, i.Scope.ResourceId!)).ToArray());
        return TenantManagementReadRules.Build(reader, reader.Baseline, args, data, sources);
    }
}
