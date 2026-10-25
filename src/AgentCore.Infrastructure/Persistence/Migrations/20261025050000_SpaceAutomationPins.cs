using AgentCore.Infrastructure.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class SpaceAutomationPins : Migration
{
    private const string PinShape = "(PinnedSpaceId IS NULL AND PinnedEnvironmentId IS NULL) OR (TenantId IS NOT NULL AND PinnedSpaceId IS NOT NULL AND lower(PinnedSpaceId) <> '00000000-0000-0000-0000-000000000000' AND (PinnedEnvironmentId IS NULL OR lower(PinnedEnvironmentId) <> '00000000-0000-0000-0000-000000000000'))";
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, true);
    protected override void Down(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, false);

    private static void Rebuild(MigrationBuilder migrationBuilder, bool up)
    {
        if (!up) migrationBuilder.Sql("""
            CREATE TEMP TABLE SpaceAutomationPinDowngradeGuard(ExistingPins INTEGER CHECK(ExistingPins=0));
            INSERT INTO SpaceAutomationPinDowngradeGuard SELECT
                (SELECT count(*) FROM Automations WHERE PinnedSpaceId IS NOT NULL OR PinnedEnvironmentId IS NOT NULL)
                +(SELECT count(*) FROM TriggerOccurrences WHERE PinnedSpaceId IS NOT NULL OR PinnedEnvironmentId IS NOT NULL)
                +(SELECT count(*) FROM TenantApprovalRequests WHERE json_type(PayloadJson,'$.Operation.AutomationAdmission.Current.PinnedSpaceId')='text'
                    OR json_type(PayloadJson,'$.Operation.AutomationMutation.Proposed.PinnedSpaceId')='text');
            DROP TABLE SpaceAutomationPinDowngradeGuard;
            """);
        var previous = ReadFrozen("20261025050000_SpaceAutomationPins.Previous.json", "050198d50553b8f2d9fa61746c16554d23bda3cf06bb1d7b433167ca87781447");
        var approvals = ReadFrozen("20261025040000_SpaceWorkspaceOwnership.Previous.json", "9e94cea85a786056cc3ff67ff99b56de9df6ce9faa76d2b529d35cc140eb4dc7");
        var sql = new StringBuilder();
        // Cross-table guards refer to these roots, so preserve all definitions during SQLite's rebuild.
        foreach (var key in previous.Keys.Where(key => key.StartsWith("trigger:", StringComparison.Ordinal)))
            sql.AppendLine("DROP TRIGGER " + key[8..] + ";");
        if (!up) foreach (var name in NewGuardNames) sql.AppendLine("DROP TRIGGER " + name + ";");
        foreach (var table in new[] { "Automations", "TriggerOccurrences", "TenantApprovalRequests" })
        {
            var pinnedTable = table != "TenantApprovalRequests";
            var source = pinnedTable ? previous["table:" + table]
                : TenantPublicDownloads.ReplaceCheck(TenantPublicDownloads.FrozenPredecessor().Previous["table:TenantApprovalRequests"],
                    up ? SpaceAutomationPinApprovalPersistence.ValuesConstraint : SpaceWorkspaceApprovalPersistence.ValuesConstraint);
            var temporary = "__space_pins_" + table;
            var columns = source.Split('\n').Where(line => line.StartsWith("    \"", StringComparison.Ordinal))
                .Select(line => "\"" + line.TrimStart().Split('"')[1] + "\"").ToArray();
            var ddl = source.Replace("CREATE TABLE \"" + table + "\"", "CREATE TABLE \"" + temporary + "\"", StringComparison.Ordinal);
            if (up && pinnedTable)
            {
                var index = ddl.IndexOf("\n    CONSTRAINT", StringComparison.Ordinal);
                if (index < 0) throw new InvalidOperationException("Frozen Automation root constraints are missing.");
                var extra = "\n    \"PinnedSpaceId\" TEXT NULL,\n    \"PinnedEnvironmentId\" TEXT NULL,"
                    + "\n    CONSTRAINT \"CK_" + table + "_SpacePins\" CHECK (" + PinShape + "),"
                    + "\n    CONSTRAINT \"FK_" + table + "_Spaces_TenantId_PinnedSpaceId\" FOREIGN KEY (\"TenantId\", \"PinnedSpaceId\") REFERENCES \"Spaces\" (\"TenantId\", \"SpaceId\") ON DELETE RESTRICT,"
                    + "\n    CONSTRAINT \"FK_" + table + "_SpaceEnvironments_TenantId_PinnedSpaceId_PinnedEnvironmentId\" FOREIGN KEY (\"TenantId\", \"PinnedSpaceId\", \"PinnedEnvironmentId\") REFERENCES \"SpaceEnvironments\" (\"TenantId\", \"SpaceId\", \"EnvironmentId\") ON DELETE RESTRICT,";
                ddl = ddl[..index] + extra + ddl[index..];
            }
            sql.AppendLine(ddl.TrimEnd(';') + ";");
            var names = string.Join(",", columns);
            sql.AppendLine("INSERT INTO \"" + temporary + "\" (" + names + (up && pinnedTable ? ",PinnedSpaceId,PinnedEnvironmentId" : "")
                + ") SELECT " + names + (up && pinnedTable ? ",NULL,NULL" : "") + " FROM \"" + table + "\";");
            sql.AppendLine("DROP TABLE \"" + table + "\"; ALTER TABLE \"" + temporary + "\" RENAME TO \"" + table + "\";");
        }
        foreach (var value in previous.Concat(approvals.Where(item => item.Key.StartsWith("index:", StringComparison.Ordinal)))
            .Where(item => item.Key.StartsWith("index:", StringComparison.Ordinal)).GroupBy(item => item.Key).Select(group => group.First().Value))
            sql.AppendLine(value.TrimEnd(';') + ";");
        if (up)
            foreach (var table in new[] { "Automations", "TriggerOccurrences" })
                sql.AppendLine("CREATE INDEX \"IX_" + table + "_TenantId_PinnedSpaceId_PinnedEnvironmentId\" ON \"" + table + "\" (\"TenantId\", \"PinnedSpaceId\", \"PinnedEnvironmentId\");");
        foreach (var item in previous.Where(item => item.Key.StartsWith("trigger:", StringComparison.Ordinal)))
            sql.AppendLine((up ? ExtendEventSnapshotGuard(item.Key[8..], item.Value) : item.Value).TrimEnd(';') + ";");
        if (up) sql.AppendLine(PinGuards());
        migrationBuilder.Sql("PRAGMA foreign_keys=0;", suppressTransaction: true);
        migrationBuilder.Sql(sql.ToString());
        migrationBuilder.Sql("PRAGMA foreign_keys=1;", suppressTransaction: true);
    }
    private static Dictionary<string, string> ReadFrozen(string name, string sha)
    {
        using var stream = typeof(SpaceAutomationPins).Assembly.GetManifestResourceStream("AgentCore.Infrastructure.Persistence.Migrations.Sql." + name)
            ?? throw new InvalidOperationException("Frozen Automation pin predecessor is missing.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); var bytes = memory.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha) throw new InvalidOperationException("Frozen Automation pin predecessor changed.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(bytes)!;
    }

}
