namespace AgentCore.Infrastructure.Security;
internal static class ManagementInspectionApprovalPersistence
{
    internal static string Extend(string prior)
    {
        const string old = "IN ('management.inspect','settings.list','settings.inspect','automation.preferences.inspect')";
        if (!prior.Contains(old, StringComparison.Ordinal)) throw new InvalidOperationException("Expected incumbent management read constraint.");
        var expanded = prior.Replace(old, "IN ('management.inspect','settings.list','settings.inspect','automation.preferences.inspect','harness.inspect','configuration.inspect')", StringComparison.Ordinal);
        return $"({expanded}) AND COALESCE(({Sources}),0)";
    }
    private const string Sources = """
        json_extract(PayloadJson,'$.Operation.ManagementRead') IS NULL OR
        (json_extract(PayloadJson,'$.Operation.ManagementRead.ToolName') IN ('harness.inspect','configuration.inspect')
          AND json_type(PayloadJson,'$.Operation.ManagementRead.SourcesJson')='text'
          AND length(CAST(json_extract(PayloadJson,'$.Operation.ManagementRead.SourcesJson') AS BLOB)) BETWEEN 2 AND 24000
          AND json_valid(json_extract(PayloadJson,'$.Operation.ManagementRead.SourcesJson'))
          AND json_type(json_extract(PayloadJson,'$.Operation.ManagementRead.SourcesJson'))='array'
          AND json_array_length(json_extract(PayloadJson,'$.Operation.ManagementRead.SourcesJson'))<=64)
        OR (json_extract(PayloadJson,'$.Operation.ManagementRead.ToolName') NOT IN ('harness.inspect','configuration.inspect')
          AND json_extract(PayloadJson,'$.Operation.ManagementRead.SourcesJson') IS NULL)
        """;
}
