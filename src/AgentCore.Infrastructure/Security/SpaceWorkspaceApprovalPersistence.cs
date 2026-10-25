namespace AgentCore.Infrastructure.Security;

// Extend the frozen predecessor shape, retaining every unrelated operation bound.
internal static class SpaceWorkspaceApprovalPersistence
{
    private static readonly Lazy<string> Frozen = new(() =>
    {
        using var stream = typeof(SpaceWorkspaceApprovalPersistence).Assembly.GetManifestResourceStream(
            "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261025040000_SpaceWorkspaceOwnership.Check.sql")
            ?? throw new InvalidOperationException("Frozen workspace approval constraint is missing.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); var bytes = memory.ToArray();
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != "1b8ef363c29524652a6843165426d6a51cf0b5769f62999804bbb52039042fd0")
            throw new InvalidOperationException("Frozen workspace approval constraint changed.");
        return System.Text.Encoding.UTF8.GetString(bytes);
    });
    internal static string ValuesConstraint => Frozen.Value;
    private const string Active = "json_extract(PayloadJson, '$.Operation.SpaceContext.SpaceId')";
    private const string Private = "json_extract(PayloadJson, '$.Operation.SpaceContext.PrivateSpaceId')";
    private const string ReadPath = "json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path')";
    private const string AccountPath = "json_extract(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson'),'$.path')";
    private const string MutationPath = """
        CASE json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName')
            WHEN 'workspace.copy' THEN json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson'), '$.destination')
            WHEN 'workspace.move' THEN json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson'), '$.source')
            WHEN 'workspace.batch' THEN json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson'), '$[0].Root')
            ELSE json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson'), '$.path') END
        """;
    private static string Home(string path) => "'home:' || CASE WHEN " + path + " = '/private' OR substr(" + path + ",1,9) = '/private/' THEN " + Private + " ELSE " + Active + " END";
    private static string Replace(string input, string old, string next)
    {
        if (!input.Contains(old, StringComparison.Ordinal)) throw new InvalidOperationException("Frozen workspace approval shape changed.");
        return input.Replace(old, next, StringComparison.Ordinal);
    }
    internal static string Extend(string prior)
    {
        var value = Replace(prior,
            "CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceRead.Working') = 1 THEN 'working:' || SessionId ELSE 'home:' || InstanceId END",
            "CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceRead.Working') = 1 THEN 'working:' || SessionId ELSE " + Home(ReadPath) + " END");
        value = Replace(value,
            "CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceMutation.Working') = 1 THEN 'working:' || SessionId ELSE 'home:' || InstanceId END",
            "CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceMutation.Working') = 1 THEN 'working:' || SessionId ELSE " + Home(MutationPath) + " END");
        value = Replace(value,
            "WHEN substr(" + AccountPath + ",1,9) = '/working/' THEN 'working:' || SessionId ELSE 'home:' || InstanceId END",
            "WHEN substr(" + AccountPath + ",1,9) = '/working/' THEN 'working:' || SessionId ELSE " + Home(AccountPath) + " END");
        value = Replace(value,
            "LIKE CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceMutation.Working') = 1 THEN '/working/%' ELSE '/home/%' END",
            "LIKE CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceMutation.Working') = 1 THEN '/working/%' WHEN substr(" + MutationPath + ",1,9) = '/private/' THEN '/private/%' ELSE '/home/%' END");
        const string artifactPath = "json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path')";
        return Replace(value, "ELSE substr(" + artifactPath + ",1,6) IS '/home/' END",
            "ELSE (substr(" + artifactPath + ",1,6) IS '/home/' OR substr(" + artifactPath + ",1,9) IS '/private/') END");
    }
}
