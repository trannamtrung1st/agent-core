using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class TenantHarnessCommandAuthoring : Migration
{
    internal static string ValuesConstraint => Read("Check.sql");
    internal static string ReceiptConstraint => Read("Receipt.sql");
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, true);
    protected override void Down(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, false);
    private static string Read(string file)
    {
        using var stream = typeof(TenantHarnessCommandAuthoring).Assembly.GetManifestResourceStream("AgentCore.Infrastructure.Persistence.Migrations.Sql.20261023120000_TenantHarnessCommandAuthoring." + file)
            ?? throw new InvalidOperationException("Frozen Harness migration source is missing.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private static void Rebuild(MigrationBuilder migrationBuilder, bool up)
    {
        var frozen = Read("Previous.json");
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(frozen))) != "14c9e9915075d0cc9cc0be62b0595a0c0462437ad65782bf628c81996b7ed318") throw new InvalidOperationException("Frozen Harness predecessor metadata changed.");
        var previous = JsonSerializer.Deserialize<Dictionary<string,string>>(frozen)!;
        var sql = new StringBuilder();
        if (!up) sql.AppendLine("""
            CREATE TEMP TABLE HarnessDowngradeGuard (Rows INTEGER CHECK(Rows=0));
            INSERT INTO HarnessDowngradeGuard SELECT (SELECT count(*) FROM TenantApprovalRequests WHERE json_type(PayloadJson,'$.Operation.ManagementMutation.Harness')='object')
              +(SELECT count(*) FROM TenantOperationReceipts WHERE json_extract(ManagementResourceJson,'$.HarnessChangeId') IS NOT NULL);
            DROP TABLE HarnessDowngradeGuard;
            """);
        foreach (var trigger in previous.Keys.Where(k => k.StartsWith("trigger:", StringComparison.Ordinal))) sql.AppendLine("DROP TRIGGER IF EXISTS \"" + trigger[8..] + "\";");
        sql.AppendLine("DROP TRIGGER IF EXISTS TR_TenantApprovals_HarnessCommand_INSERT; DROP TRIGGER IF EXISTS TR_TenantOperationReceipts_HarnessAssessment_INSERT; DROP TRIGGER IF EXISTS TR_TenantOperationReceipts_HarnessAssessment_UPDATE;");
        foreach (var table in new[] { "TenantApprovalRequests", "TenantOperationReceipts" })
        {
            var ddl = previous["table:" + table];
            if (up) ddl = ReplaceCheck(ddl, table == "TenantApprovalRequests" ? "CK_TenantApprovalRequest_Values" : "CK_TenantOperationReceipt_ManagementResource",
                table == "TenantApprovalRequests" ? ValuesConstraint : ReceiptConstraint);
            var temporary = "__harness_" + table;
            var name = ddl.IndexOf(table, StringComparison.Ordinal);
            ddl = ddl[..name] + temporary + ddl[(name + table.Length)..];
            sql.AppendLine(ddl + ";"); sql.AppendLine("INSERT INTO \"" + temporary + "\" SELECT * FROM \"" + table + "\";");
        }
        sql.AppendLine("DROP TABLE TenantOperationReceipts; DROP TABLE TenantApprovalRequests;");
        foreach (var table in new[] { "TenantApprovalRequests", "TenantOperationReceipts" }) sql.AppendLine("ALTER TABLE \"__harness_" + table + "\" RENAME TO \"" + table + "\";");
        foreach (var item in previous.Where(i => i.Key.StartsWith("index:", StringComparison.Ordinal))) sql.AppendLine(item.Value + ";");
        foreach (var item in previous.Where(i => i.Key.StartsWith("trigger:", StringComparison.Ordinal)))
        {
            var guard = item.Value;
            if (up && item.Key == "trigger:TR_TenantOperationReceipts_ManagementResource_INSERT") guard = guard.Replace(
                "AND json_extract(NEW.ManagementResourceJson,'$.Result.Revision')=1\n   OR NEW.RequestId", "AND json_extract(NEW.ManagementResourceJson,'$.Result.Revision')=1\n   OR NEW.RequestId IS NULL AND json_extract(NEW.ManagementResourceJson,'$.HarnessChangeId') IS NOT NULL\n   OR NEW.RequestId");
            sql.AppendLine(guard + ";");
        }
        if (up) sql.AppendLine(Read("Guards.sql"));
        migrationBuilder.Sql("PRAGMA foreign_keys=0;", suppressTransaction:true);
        migrationBuilder.Sql(sql.ToString());
        migrationBuilder.Sql("PRAGMA foreign_keys=1;", suppressTransaction:true);
    }
    private static string ReplaceCheck(string ddl, string constraint, string next)
    {
        var name = ddl.IndexOf(constraint, StringComparison.Ordinal);
        if (name < 0) throw new InvalidOperationException("Frozen predecessor constraint is missing.");
        var start = ddl.IndexOf('(', ddl.IndexOf("CHECK", name, StringComparison.Ordinal)); var depth = 1; var quoted = false;
        for (var i = start + 1; i < ddl.Length; i++)
        {
            if (ddl[i] == '\'') { if (quoted && i + 1 < ddl.Length && ddl[i + 1] == '\'') { i++; continue; } quoted = !quoted; }
            if (quoted) continue;
            if (ddl[i] == '(') depth++; else if (ddl[i] == ')' && --depth == 0) return ddl[..(start + 1)] + next + ddl[i..];
        }
        throw new InvalidOperationException("Frozen predecessor constraint is unbalanced.");
    }
}
