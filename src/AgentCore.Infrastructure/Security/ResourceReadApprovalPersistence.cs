namespace AgentCore.Infrastructure.Security;

internal static class ResourceReadApprovalPersistence
{
    internal static string Extend(string prior)
    {
        // Extend the incumbent read variant, preserving its closed payload and observation bounds.
        const string primary = "Permission = 'workspaces.read' AND ResourceType = 'workspace'";
        const string address = "ResourceId IS CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceRead.Working') = 1 THEN 'working:' || SessionId ELSE 'home:' || InstanceId END";
        if (!prior.Contains(primary, StringComparison.Ordinal) || !prior.Contains(address, StringComparison.Ordinal))
            throw new InvalidOperationException("Workspace consent persistence shape changed.");
        return prior.Replace(primary, "((Permission = 'workspaces.read' AND ResourceType = 'workspace' AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS NULL) OR (" + Resource + "))", StringComparison.Ordinal)
            .Replace(address, "((Permission = 'workspaces.read' AND " + address + ") OR (Permission = 'resources.read' AND ResourceId IS InstanceId || ':' || json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')))", StringComparison.Ordinal);
    }
    private const string Resource = """
        Permission = 'resources.read' AND ResourceType = 'resource'
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS 'text'
        AND ((length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')) = 45 AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),1,9) IS 'instance:')
            OR (length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')) = 47 AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),1,11) IS 'definition:'))
        AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-36) NOT GLOB '*[^0-9a-f-]*'
        AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-28,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-23,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-18,1) IS '-'
        AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-13,1) IS '-'
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') >= 0
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'false'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IN ('workspace.read','office.document.inspect','office.spreadsheet.inspect')
        AND (substr(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path'),1,17) IS '/agent/resources/'
            OR substr(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path'),1,26) IS '/agent/instance/resources/')
        """;
}
