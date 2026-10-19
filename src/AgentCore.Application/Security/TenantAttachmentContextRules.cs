using AgentCore.Application.Agents;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Security;

public static class TenantAttachmentContextRules
{
    public const int MaxSources = PromptContextBuilder.MaxManifestFiles;
    public static TenantAttachmentContextSource Witness(AttachmentRecord item, string? processor = null) =>
        new(item.AttachmentId, item.EntryId ?? throw new AccessDeniedException("attachment_source_changed"), item.DisplayName, item.ContentType,
            item.ByteSize, item.Sha256Hex, processor);
    public static IReadOnlyList<TenantAttachmentContextSource> Merge(IReadOnlyList<TenantAttachmentContextSource>? saved, IReadOnlyList<TenantAttachmentContextSource>? fresh)
    {
        foreach (var group in new[] { saved, fresh })
            if (group is not null && (group.Count > MaxSources || group.Any(item => item is null) || group.Select(item => item.AttachmentId).Distinct().Count() != group.Count))
                throw new AccessDeniedException("attachment_context_limit");
        var result = new Dictionary<Guid, TenantAttachmentContextSource>();
        foreach (var item in (saved ?? []).Concat(fresh ?? []))
        {
            if (item.AttachmentId == Guid.Empty || item.EntryId == Guid.Empty || item.DisplayName is not { Length: > 0 and <= 200 } || item.ContentType is not { Length: > 0 and <= 200 }
                || item.ByteSize is < 0 or > AgentCore.Domain.Conversation.AttachmentLimits.MaxBytesEach || item.Sha256Hex is not { Length: 64 }
                || item.Sha256Hex.Any(c => !char.IsAsciiHexDigitLower(c)) || item.ProcessorVersion is { Length: < 1 or > 128 })
                throw new AccessDeniedException("attachment_source_changed");
            if (result.TryGetValue(item.AttachmentId, out var prior))
            {
                if (prior with { ProcessorVersion = null } != item with { ProcessorVersion = null }
                    || prior.ProcessorVersion is not null && item.ProcessorVersion is not null && prior.ProcessorVersion != item.ProcessorVersion)
                    throw new AccessDeniedException("attachment_source_changed");
                if (prior.ProcessorVersion is not null) continue;
            }
            result[item.AttachmentId] = item;
        }
        if (result.Count > MaxSources) throw new AccessDeniedException("attachment_context_limit");
        return Array.AsReadOnly(result.Values.ToArray());
    }
}
